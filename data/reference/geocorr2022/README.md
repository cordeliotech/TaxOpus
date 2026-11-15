# ZIP code crosswalks (Geocorr 2022)

Population-weighted links from ZIP Code Tabulation Areas (ZCTAs) to counties and to places (cities, towns,
CDPs), built from the 2020 Census by the Missouri Census Data Center's Geocorr 2022:
https://mcdc.missouri.edu/applications/geocorr2022.html

Generated 2026-10-25 for all 50 states and DC with weighting variable `pop20` (2020 Census population):

- `zcta_to_county.csv`: source `zcta`, target `county`
- `zcta_to_place.csv`: source `zcta`, target `place`

Both files have two header rows (column names, then labels) and are Latin-1 encoded. `afact` is the share of the
ZCTA's 2020 population in the county or place. Place code `99999` is the part of a ZCTA outside any place; names
ending in "CDP" are Census-designated places, which are not taxing jurisdictions.

The data is fixed to 2020 Census geography, so there is no need to regenerate it until the 2030 Census. To rebuild
it, run the same request (`g1_=zcta`, `g2_=county` or `place`, `wtvar=pop20`, `filefmt=csv`, every state selected).

Caveats: ZCTAs approximate USPS ZIP codes (PO-box-only ZIPs have no ZCTA), and place boundaries are as of 2020.
