# Contributing to TaxOpus

Thank you for contributing to TaxOpus!

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Building & Testing

```bash
dotnet restore TaxOpus.slnx
dotnet build TaxOpus.slnx --no-restore
dotnet test TaxOpus.slnx --no-build
```

All unit tests run offline using fixtures in `tests/TaxOpus.Tests/Fixtures/` and reference tables in `data/reference/`.

## Updating Tax Rates & State Sources

1. **Curated local rates (`HI`, `ID`, `LA`, `MS`, `NY`, `PA`, `SC`)**:
   - Edit `data/reference/curated_local_rates.csv`.
   - Always cite the official state/parish URL and effective date (`yyyy-MM-dd`) in the `EffectiveFrom` and `Source` columns.
2. **New or updated state sources**:
   - Implement `IStateRateSource` (and `IPointRateSource` if boundary-based) in `src/TaxOpus.Core/Sources/` and register it in `StateRateSources.All`.
   - Add unit tests with real sample rows/fixtures in `tests/TaxOpus.Tests/`.
   - Ensure the source respects `--as-of` where upstream data supports it and falls back to cached raw files on transient download failures.

## Pull Request Guidelines

- Keep commits focused and descriptive.
- Ensure `dotnet test TaxOpus.slnx` passes with zero warnings/errors.
- Do not commit `data/raw/` files (they are gitignored and can be several gigabytes).
