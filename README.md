# TaxOpus

A high-performance US sales tax ingestion and export utility.

## Overview
- **SST Ingestion**: Downloads and parses the rate and boundary files the Streamlined Sales Tax Governing Board (SSTGB) publishes for its 24 member states, and computes one combined rate per ZIP code (state + county + city + special districts) as of a given date.
  - A ZIP that spans jurisdictions gets the rate of most of its address rows; `MinCombinedRatePercent` and `MaxCombinedRatePercent` give the range found inside the ZIP.
  - States whose files have no ZIP-level rows (Indiana, Kentucky, Michigan, New Jersey, Rhode Island publish one statewide row) fall back to the curated statewide rate.
- **State Sources** (`Sources/`): ZIP-level rates for states outside SST, from each state's own published data:

  | State | Source | How ZIPs get rates |
  |---|---|---|
  | Missouri | DOR rate and boundary databases (quarterly) | Address/ZIP+4 boundary rows, like SST; sales tax rate per DOR jurisdiction code |
  | Illinois | IDOR address-specific rate file (~94M addresses, 1.8 GB) | Rate of most addresses in the ZIP; combined rates only |
  | Florida | DOR discretionary surtax table (county) | ZIP-to-county by population |
  | Virginia | Virginia Tax rates by locality workbook | ZIP-to-county (independent cities are county-equivalents) by population |
  | Alaska | ARSSTC monthly rate sheet by ZIP (member jurisdictions) | Split between inside- and outside-city rates by population inside city limits |
  | Arizona | ADOR monthly TPT rate table, retail class | County rate (includes state) plus city rate; ZIP-to-county and ZIP-to-city by population |
  | New Mexico | TRD gross receipts tax GIS layer for the July–June period (public domain) | County remainder and municipal rates; ZIP-to-county and ZIP-to-city by population |
  | Hawaii, Idaho, Louisiana, Mississippi, New York, Pennsylvania, South Carolina | `data/reference/curated_local_rates.csv`, from the states' and parish/LATA web pages and PDFs (New York: Publication 718; Idaho: each resort city's own pages, for the cities on the Tax Commission's list; Louisiana: LATA parish and municipal tables) | ZIP-to-county and ZIP-to-city by population |
  | California | CDTFA sales and use tax rate areas (ArcGIS layer, quarterly) | Rate area containing each Census block |
  | Colorado | CDOR location code workbook (half-yearly), placed on Census county, DOLA municipal and special district, and Pikes Peak RTA boundaries; `data/reference/colorado_location_codes.csv` places codes the workbook name doesn't | Location code at each Census block |
  | Alabama | ALDOR city and county rate file (monthly), placed on Census city limits, police jurisdictions computed from the 2021 city limits (3 miles for cities of 6,000 or more people, 1.5 miles otherwise, nearest city where they overlap) and `data/reference/alabama_county_zones.csv` (county taxes that apply only inside or outside certain cities) | Rate at each Census block |
  | Texas | Comptroller's permitted sales tax locations (about a million, Texas Open Data Portal) with the city, county, transit and special district authorities at each, priced with the Comptroller's quarterly rate file | Rate of most business locations in the ZIP |

  ZIP-to-county and ZIP-to-city shares come from Geocorr 2022 (2020 Census population), stored in `data/reference/geocorr2022`. California, Colorado and Alabama instead place every 2020 Census block in the rate boundaries and weight each ZIP's rates by block population; the first online run downloads the state's block file (200–400 MB) and the national block-to-ZIP file (1 GB) into `data/raw/census` and caches each state's blocks as `blocks_<fips>.csv`. The `lookup` command gives the rate at a point in these states. The curated table must be updated by hand when those states change a rate (South Carolina revises ST-500 for May 1, March 1 and August 1 changes; New York changes rates only on March 1, June 1, September 1 or December 1; Idaho's cities change theirs on January 1 after November votes). Online runs warn when New York lists a newer Publication 718 than the one copied, or when the Idaho Tax Commission updates its list of cities with a local tax.
- **Curated Baselines**: CT, DC, ME, MD and MA have no general local sales tax; DE, MT, NH and OR have no general sales tax. Other states fall back to `BaselineStateTaxProvider` only if their live/curated source is unavailable and no prior export exists.
- **Tenant-Level Seed Models**: Builds normalized `TenantStateTaxSeed` profiles (rates, ZIP rules, and statewide catch-alls) from ZIP-level rates where a state has them and from the curated baseline elsewhere, previewable as JSON via `seed-tenant`.
- **Caveats**: Rates are per ZIP code, not per address; `MinCombinedRatePercent`/`MaxCombinedRatePercent` show where a ZIP spans several. Illinois, Missouri, Arizona and Texas tax in-state sellers at the seller's location rather than the buyer's. Florida's surtax applies only to the first $5,000 of each item. Alaska's non-member local taxes are not in the ARSSTC sheet.
  - Colorado: tax.colorado.gov blocks scripted downloads, so each half-year's workbook (January and July) has to be saved into `data/raw/co/` from a browser; until it is, runs use the previous one and warn. Codes for special-tax and improvement-district areas with no public boundary (about 40 of 447) fall back to the surrounding code. Home-rule cities that collect their own tax are included at their published rate, but their tax bases may differ.
  - Texas: ZIPs are weighted by business locations rather than residents, so residential areas with few businesses (and their special districts) count for less. ZIPs that aren't 2020 Census ZIP areas need at least 10 locations, which drops mistyped ZIPs. Runs for a past quarter use that quarter's rates with the current locations. The Comptroller's full address dataset, which needs a free account on its file transfer site (data-secure.comptroller.texas.gov), would weight by every address.
  - Idaho: only city taxes on all retail sales are included, not those on lodging, liquor by the drink or restaurant food alone; the cities, not the Tax Commission, collect them. Most of these cities hold a minority of their ZIP's people, so their rate shows only as the ZIP's maximum.
  - Alabama: police jurisdiction rates are the full PJ rate; cities that reduce them in half-mile steps, and districts inside cities (Spanish Fort's), are not modelled.

## Project Structure
```
TaxOpus/
├── src/
│   ├── TaxOpus.Core/           # Core domain models, parsers, state sources and exporters
│   │   ├── Models/             # TaxJurisdictionRate, StateMetadata, TenantSeedModels
│   │   ├── Downloader/         # SstDownloader (HTTP/Zip extraction), SstFileName (file ordering)
│   │   ├── Parsers/            # SstParser, ZipRateAggregator, XlsxReader
│   │   ├── Data/               # BaselineStateTaxProvider, ZipTenantSeedProvider, ZIP crosswalk
│   │   ├── Geo/                # Polygons, shapefiles, GeoJSON and Census blocks for boundary-based states
│   │   ├── Sources/            # Non-SST state sources (MO, IL, FL, VA, AK, AZ, NM, CA, CO, AL, TX, curated HI/ID/MS/NY/PA/SC)
│   │   ├── Exporters/          # MasterJsonExporter, MasterCsvExporter, SQL Exporters
│   │   └── Services/           # TaxPipelineService orchestrator
│   └── TaxOpus.Cli/            # Command-line interface
├── tests/
│   └── TaxOpus.Tests/          # Unit tests (xUnit); Fixtures hold rows and files copied from the states' published data
└── data/
    ├── raw/                    # Downloaded state files: sst/, mo/, il/, fl/, va/, ak/, az/, nm/, ca/, co/, al/, tx/, census/ (not checked in)
    ├── reference/              # Geocorr ZIP crosswalks, the curated local rate table, Colorado and Alabama mappings
    └── exports/                # Generated master datasets
        ├── us_sales_tax_combined.json
        ├── us_sales_tax_combined.csv
        └── by_state/           # Individual state JSON slices
```

## Usage

### Run Full Pipeline
Downloads the latest SST and state files, computes combined rates, and generates all export formats:
```bash
DOTNET_CLI_HOME=$HOME dotnet run --project src/TaxOpus.Cli/TaxOpus.Cli.csproj -- all
```

To rebuild the exports from the files already in `data/raw` without downloading, use `process`. Add `--as-of yyyy-MM-dd` to compute rates for another date (default: today, UTC):
```bash
DOTNET_CLI_HOME=$HOME dotnet run --project src/TaxOpus.Cli/TaxOpus.Cli.csproj -- process --as-of 2026-10-24
```

### Preview Tenant Seed Script for a State
```bash
DOTNET_CLI_HOME=$HOME dotnet run --project src/TaxOpus.Cli/TaxOpus.Cli.csproj -- seed-tenant TX
```

### Look Up the Rate at a Point
California, Colorado and Alabama answer for a longitude and latitude, from the cached files. Pass one point, or `longitude,latitude` lines on stdin; each answer is `longitude,latitude,combined,state,county,city,district,county FIPS`:
```bash
DOTNET_CLI_HOME=$HOME dotnet run --project src/TaxOpus.Cli/TaxOpus.Cli.csproj -- lookup AL -86.8104 33.5186
```

### Keep Rates Up to Date
Re-run the pipeline (`all` or `process`). If a state's data can't be fetched or read (a site is down, a file is missing or has changed format), the run keeps that state's rates from the last export instead of dropping it to the curated baseline. The run warns and lists the state in its summary, and `StateRatesAsOf` in `us_sales_tax_combined.json` gives the date those rates were computed for. Files a state replaces under the same name (Alabama's and Virginia's monthly files) fall back to the cached copy when they can't be downloaded again.

### Run Unit Tests
```bash
DOTNET_CLI_HOME=$HOME dotnet test TaxOpus.slnx
```

## Contributing & Security
- See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, test commands, and guidelines for updating state sources or curated rates.
- See [SECURITY.md](SECURITY.md) for reporting security vulnerabilities.

## Disclaimer
TaxOpus aggregates public sales tax data at the ZIP code (or Census block) level for convenience. Tax rates, boundaries, exemptions, sourcing rules, and special districts change frequently; this project is provided for informational purposes only and does not constitute tax or legal advice.

## License
Licensed under the [MIT License](LICENSE).
