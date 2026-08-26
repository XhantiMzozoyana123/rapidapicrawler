# Rapid API Crawler — Competitor Research & Gap Analysis

A .NET MAUI app + Clean Architecture backend that scrapes the **RapidAPI marketplace**
for a given keyword, captures competitor API pages, stores everything in **MySQL**,
and runs the gap-analysis report through a **local LLM (LLamaSharp)** — no API keys, fully self-hosted on your VPS.
telling you exactly what you should build.

## Solution layout

| Project | Layer | Responsibility |
|---------|-------|----------------|
| `RapidApiCrawler.Domain` | Domain | Entities: `SearchRun`, `ApiListing`, `CrawledPage`, `AnalysisReport` |
| `RapidApiCrawler.Application` | Application | Ports (interfaces) + `CrawlOrchestrator` implementing the crawl flow |
| `RapidApiCrawler.Infrastructure` | Infrastructure | **Playwright** scraper, **MySQL** repository, **LLamaSharp** local LLM, CSV exporter |
| `RapidApiCrawler` | Presentation | MAUI UI: keyword input, live progress log, report viewer |
| `RapidApiCrawler.Web` | Presentation | **ASP.NET Core MVC** web app (same layers) — crawl from a browser, browse data, download CSVs |
| `RapidApiCrawler.Cli` | Tooling | Console harness to run/validate the crawler headless, import from SQLite, export CSVs |

## ASP.NET Core MVC version (`RapidApiCrawler.Web`)

A full web UI over the same clean-architecture layers — no duplicated scraping logic.

```powershell
dotnet run --project RapidApiCrawler.Web
# then open the URL it prints (e.g. https://localhost:7xxx)
```

Pages:

| Page | What you can do |
|------|-----------------|
| **Home** | Start a crawl (keyword + AI toggle + headless toggle). The crawl runs in the background and the page polls `/Home/Progress` for live log output. Also shows recent runs. |
| **Listings** | Every scraped API listing, filterable by run, with links back to RapidAPI. |
| **Database** | Browse any SQLite table as an HTML grid (300 rows/page), with a per-table CSV download button. |
| **Report** | The local-LLM gap-analysis report per run. |
| **Export CSV (zip)** | Download every table as one ZIP of CSVs; per-table downloads too. |

Notes:
- The web app connects to **MySQL** — set `MYSQL_CONNECTION_STRING` env var, or configure
  in `appsettings.json` under `"MySql": { "ConnectionString": "..." }`.
- Configure the local LLM with `LLAMA_MODEL_PATH` (a .gguf model file) or in
  `appsettings.json` under `"Llama": { "ModelPath": "...", "ContextSize": 4096 }`.
  Recommended: Qwen2.5-7B-Instruct / Mistral-7B-Instruct / Llama-3.1-8B-Instruct (Q4/Q5 GGUF).
- See **docker-compose.yml** — run `docker compose up -d` to start MySQL on your VPS.
- Because the crawler drives a real browser, run this locally (not designed for a shared server).

## Docker deployment (GPU-enabled)

The repo ships a `Dockerfile` + `docker-compose.yml` that run MySQL + the web app
with full access to your NVIDIA GPU (tested target: Ubuntu 24.04 + RTX A4000 + driver 560 / CUDA 12.6).

Three layers must line up:

| Layer | Responsibility | Where |
|-------|----------------|-------|
| NVIDIA driver | controls the GPU | host |
| NVIDIA Container Toolkit | gives Docker GPU access | host |
| LLamaSharp.Backend.Cuda12 | makes the app use CUDA | container |

### One-time host setup

```bash
# NVIDIA Container Toolkit
curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey | sudo gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list | \
  sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
  | sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list
sudo apt-get update && sudo apt-get install -y nvidia-container-toolkit
sudo nvidia-ctk runtime configure --runtime=docker && sudo systemctl restart docker

# Verify Docker can see the GPU:
docker run --rm --gpus all nvidia/cuda:12.6.0-runtime-ubuntu24.04 nvidia-smi
```

### Run everything

```bash
mkdir models && cp /path/to/qwen2.5-7b-instruct-q5_k_m.gguf models/model.gguf
docker compose up -d --build
watch -n1 nvidia-smi    # VRAM should jump once the model loads after first AI analysis
```

The app listens on port **8080**. MySQL data persists in the `mysql_data` volume;
your GGUF model is mounted read-only from `./models`.

## What the crawler does (mirrors your spec)

1. Opens `https://rapidapi.com/search?term={keyword}&sortBy=ByRelevance`
2. Collects every listing link (`a.text-inherit[href*='/api/']`)
3. Opens each listing in a new browser tab
4. Clicks the **Discussions** tab and captures its HTML
5. Clicks **next** pagination until exhausted, capturing every discussions page
6. Closes the tab and moves to the next listing (search pagination repeats 2–5 per search page)

All raw HTML and metadata are persisted to **MySQL** tables
(`SearchRuns`, `ApiListings`, `CrawledPages`, `AnalysisReports`).

## Setup

### 0. Start MySQL (Linux VPS)

```bash
docker compose up -d   # brings up MySQL 8.0 with database "RapidApiCrawler"
```

Or install MySQL directly and create the database:
```sql
CREATE DATABASE RapidApiCrawler;
CREATE USER 'rapidapi'@'%' IDENTIFIED BY 'your_password';
GRANT ALL PRIVILEGES ON RapidApiCrawler.* TO 'rapidapi'@'%';
FLUSH PRIVILEGES;
```

Set your connection string (used by all three apps — Web, MAUI, CLI):
```bash
export MYSQL_CONNECTION_STRING="server=localhost;database=RapidApiCrawler;uid=rapidapi;pwd=your_password;SslMode=none;"
```

### 1. Install Playwright Chromium (one-time)

```powershell
# from RapidApiCrawler.Cli
dotnet run --project RapidApiCrawler.Cli
.\RapidApiCrawler.Cli\bin\Debug\net10.0\playwright.ps1 install chromium
```
(or, once the Playwright CLI is on PATH: `dotnet playwright install chromium`)

### 2. Local LLM model (GGUF)

Download any instruct-tuned GGUF model and point the app at it — no API key needed:

```bash
export LLAMA_MODEL_PATH="/opt/models/qwen2.5-7b-instruct-q5_k_m.gguf"
```

The model loads lazily on first analysis. The project ships with the
`LLamaSharp.Backend.Cuda12` backend - on a CUDA 12.x NVIDIA GPU (e.g. RTX A4000) it
offloads up to `GpuLayerCount` (default **999** = as many layers as VRAM allows) and
enables flash attention. Verify with `watch -n1 nvidia-smi`: VRAM should jump when the
model loads, and the app prints `[llama.*]` native logs showing CUDA init.
For CPU-only, swap to `LLamaSharp.Backend.Cpu` and set `GpuLayerCount: 0`.

Without a key the crawler still runs — it just skips the AI report.

## Run

### CLI (headless, great for testing / scheduling)

```powershell
# crawl + capture (cap at 5 listings for a quick test)
dotnet run --project RapidApiCrawler.Cli -c Debug -- "instagram scraper" --max 5 --conn "$env:MYSQL_CONNECTION_STRING"

# crawl + capture + AI gap analysis
dotnet run --project RapidApiCrawler.Cli -c Debug -- "instagram scraper" --max 20 --analyze --conn "$env:MYSQL_CONNECTION_STRING"

# print previously stored runs from MySQL
dotnet run --project RapidApiCrawler.Cli -c Debug -- --stats

# import old SQLite data into MySQL (one-time migration)
dotnet run --project RapidApiCrawler.Cli -c Debug -- --import-sqlite ./rapidapi-crawler.db --conn "$env:MYSQL_CONNECTION_STRING"

# export all tables as CSV
dotnet run --project RapidApiCrawler.Cli -c Debug -- --export
```

### MAUI app (Windows only)

The MAUI app targets **Windows** exclusively (`net10.0-windows10.0.19041.0`).
Run the `RapidApiCrawler` project in Visual Studio or with `dotnet run --project RapidApiCrawler`. Enter a keyword, optionally tick
"AI gap analysis", press **Start Crawl**, and watch the live progress. Press
**Show Last Report** to view the local-LLM gap-analysis report, or **View Database**
to browse the raw MySQL tables (`SearchRuns`, `ApiListings`, `CrawledPages`,`AnalysisReports`).

### Export data as CSV

Press **Download CSV** in the app to write every scraped table out as a `.csv`
file (one per table, `RFC-4180`-escaped so the embedded HTML survives in Excel):

- On **Windows** files are saved to your `Downloads` folder (and the app offers to
  open it for you).
- On other platforms they are written under the app's local data folder.

From the CLI, use `--export` (optionally `--export-dir <path>`):

```powershell
dotnet run --project RapidApiCrawler.Cli -c Debug -- --export
# Exported: ...\exports\AnalysisReports.csv
# Exported: ...\exports\ApiListings.csv
# Exported: ...\exports\CrawledPages.csv
# Exported: ...\exports\SearchRuns.csv
```

The exported CSVs are: `SearchRuns.csv`, `ApiListings.csv`, `CrawledPages.csv` and
`AnalysisReports.csv`.

### Headless / headed browsing

By default the scraper runs the browser **headless** (no window). To watch what
the browser is doing:

- **MAUI app:** turn off the **"Headless browser"** switch before starting a crawl,
  and set `MYSQL_CONNECTION_STRING` env var for the database connection. The browser
  is relaunched with the new setting automatically on the next run.
- **CLI:** pass `--headed` and set `MYSQL_CONNECTION_STRING` (or use `--conn`).

### Command-line options (CLI)

| Flag | Meaning |
|------|---------|
| *(arg)* | keyword to search (default `instagram scraper`) |
| `--max N` | stop after N listings (useful for quick tests) |
| `--analyze` | run the local-LLM gap analysis (requires LLAMA_MODEL_PATH) |
| `--model PATH` | GGUF model path for the local LLM (or env `LLAMA_MODEL_PATH`) |
| `--headed` | show the browser window instead of headless |
| `--stats` | print stored runs + table metadata from MySQL |
| `--conn STR` | MySQL connection string (or env `MYSQL_CONNECTION_STRING`) |
| `--import-sqlite PATH` | migrate old SQLite data into MySQL |

## Customising the local LLM

All tuning lives in `LlamaOptions`: `ContextSize`, `MaxTokens`, `GpuLayerCount`,
`ThreadCount`. Any llama.cpp-compatible GGUF model works — pick one that fits your
VPS RAM (7B-Q4 ≈ 5 GB).

## Gap-analysis report

The LLM prompt asks for a Markdown report with:

1. Market Overview
2. Competitor Landscape (table)
3. Gaps & Underserved Needs
4. Recommended APIs to Build (top 3 ideas: target users, key endpoints, differentiation)
5. Risks

Results are stored in the `AnalysisReports` MySQL table and shown in the MAUI app.

## Notes / future work

- RapidAPI is a heavy JS SPA that can rate-limit or challenge headless browsers.
  The `PlaywrightRapidApiClient` retries page loads and uses the DOM selectors from
  your spec; the selectors may need occasional updates if RapidAPI changes markup.
- A **React** dashboard for richer data analytics is an optional future addition —
  since the raw HTML + parsed metadata are already in SQLite, any frontend can read
  them via the repository layer.