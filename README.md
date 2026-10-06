# MarketMage

MarketMage is a Dalamud plugin that finds potential ways to make gil in Final Fantasy XIV. Open `/marketmage` while logged in: the default **Discover** view starts scanning automatically, without selecting items first.

## Install through Dalamud

Add this URL under `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, save, then find MarketMage in `/xlplugins`:

```text
https://github.com/vitolscolin/MarketMage/releases/latest/download/pluginmaster.json
```

This is a custom repository, not an official Dalamud listing. Disable the development copy before installing the repository version to avoid duplicate plugins. The feed and ZIP downloads are public and need no GitHub login or API key.

Maintainers: bump the project version, commit the changes, and push its matching `vX.Y.Z` tag. The release workflow runs checks, builds the plugin, and generates the feed from the built manifest (including assembly version and Dalamud API level). It publishes `MarketMage.zip`, a standalone `MarketMage.dll`, and `pluginmaster.json` together in a GitHub Release. Downloads inside each feed are pinned to that release tag; the install URL above follows the latest release. Never move an existing release tag. To roll back code, publish it under a higher version so installed clients receive an update. Publishing an older tag as latest is not supported.

## Automatic gil discovery

- Detects your **home world for selling** and **current data center for buying**. If you visit another data center, the source markets change and old results clear.
- Looks for **crafting opportunities** and **buy-on-another-world / resell-at-home opportunities**, separately for NQ and HQ.
- Screens **the entire marketable local catalog every round** using Universalis's cached aggregate endpoint, 100 item IDs per request. A 16,845-item catalog takes 169 screening requests on your home DC, instead of rotating just 100 new items every 10 minutes.
- Aggregate responses include both NQ/HQ prices and world/DC summaries. When visiting another DC, a separate source-DC screening pass is needed; home-world prices still determine sale revenue.
- Ranks preliminary signals using average sale price, current minimum listing price, estimated daily sales velocity, and ingredient costs. Aggregate unit prices are used only to select candidates—not as verified shopping plans.
- Validates the top 150 candidate items plus 50 rotating lower-ranked candidates, and up to 50 existing/watchlist items (deduplicated). Lower-ranked candidates rotate so repeated false positives cannot monopolize detailed checks.
- Publishes listing-checked results in batches of 25 and ranks qualifying plans by estimated net gil, then sampled sales activity. Separate counters show catalog items screened, items with home sales data, items with fresh home uploads, source-DC price availability/freshness, and shortlisted items checked against actual listings. Full screening coverage does not mean every item has usable data or that every listing was checked.
- Compares alternate recipes and every batch size from 1–10 crafts. Shows the recipe, crafting job/level, required craft count, and output quantity.
- Builds a **whole-stack shopping plan**, including which world to visit, quantities, unit prices, and assumed purchase tax. Different ingredients may come from different worlds in your local DC.
- Filters by gil budget per plan, minimum net profit, minimum ROI, minimum sampled sales, and maximum market-data age. Defaults: 100,000 gil budget, 1,000 gil profit, 10% ROI, three sampled sales in seven days, and data no older than 24 hours.
- Automatically starts another round 10 minutes after successful completion; a failed round retries after one minute. Pause scanning or use Scan now at any time. Closing the window, switching to manual comparison, logging out, changing scope, or unloading cancels outstanding discovery work.
- Removes findings when a rescan no longer supports them. Findings expire 15 minutes after evaluation or when their source data crosses the configured age limit.

Automatic scanning performs public market-data analysis only. MarketMage does not buy, sell, craft, gather, move, manage retainers, or interact automatically with game servers.

## Saved monitor and checklist

Select a returned opportunity and choose **Watch item** (no reservation) or **Plan this batch** (reserve output), then open **My plans**. Up to 50 active plans are saved in Dalamud configuration and prioritized for detailed checks when their sale world and source DC match your current scope.

- Mark individual shopping stacks purchased, crafting completed, listing completed, and sold/completed. Add notes or remove a plan.
- Completed plans are hidden by default; enable Show sold plans to view them.
- The original quantities, shopping plan, profit estimate, and your checklist are preserved. Later scans show the latest qualifying plan separately and never rewrite purchase progress.
- If a checked item no longer qualifies, the monitor reports **Not qualifying / data unavailable**. If it has not been checked for 15 minutes, it reports **Needs recheck**. Saved plans are not automatically deleted when market opportunities expire.
- Quantity progress and reservations are described below. Existing saved plans reserve their original output unless completed or explicitly changed to zero.
- Progress is manual: the plugin does not detect purchases, crafting, retainer listings, or completed sales. Rechecking uses the normal scanner and current filters, not automatic in-game actions.

## Quantities, comparisons, and quick refresh

- **Committed quantities:** **Plan this batch** reserves the output quantity; **Watch item** saves with zero reserved units. In the monitor, enter cumulative committed, crafted/acquired, listed, and sold output units. Unsold reservation is `max(committed, crafted/acquired, listed) - sold`, floored at zero; listed units are not added again to crafted units. Completing or removing a plan releases its reservation. To watch without reserving, set committed to zero and leave the other quantities zero. Checklist checkboxes remain manual milestones; quantity fields are the source for partial progress.
- **Remaining demand:** Reservations are grouped by sale world, item, and quality, regardless of source DC or acquisition method. If your three-day allowance is six units and four remain unsold, new crafting must fit the remaining two. Editing reservations cancels current calculations and clears findings for recalculation. The monitor's latest plan describes an **additional batch**, not a repricing of your existing unsold inventory.
- **Compare crafting batches:** Expand this section under a result to compare recipe, craft count, output, full-stack cost, net gil, ROI, sale time including commitments, and qualification reason. All 1–10 counts are retained for each checked recipe. Missing or unaffordable baskets have unknown costs. The recommended batch is marked; alternatives must not be combined as independent demand allowances.
- **Break-even and price-drop scenarios:** Under opportunities and saved plans, inspect the break-even unit sale price and profits at unchanged, 5%, 10%, and 20% lower prices. Calculations use full purchase outlay and the same per-unit 5% sale-tax rounding as the engine. They assume every output sells and do not predict demand or sale time at a changed price.
- **Targeted refresh:** Use Refresh this item now or Refresh tracked items now. This cancels a running scan and retrieves selected output aggregates, detailed world/DC listings, history, and required ingredients without full-catalog screening. Selected aggregate reads bypass the plugin cache, though provider-side data may still be cached. Provider pacing, cancellation, scope checks, and freshness rules still apply. Other findings remain subject to normal expiry. If automatic scanning is enabled, the next scheduled round returns to full discovery.

## Estimated time to sell a batch

The units/day column also shows a batch sale-time scenario:

`days = output quantity / (home-world units per day × assumed share of sales)`

The visible share slider defaults to **25%**, an editable assumption rather than a measured personal market share. For example, 10 units at 2 units/day takes 5 days at 100% of market sales, or 20 days at a 25% share. Missing, zero, or invalid velocity shows Unknown, not an instant sale. Very slow batches can show more than a year.

Crafting recommendations are capped by this estimate before choosing a batch; resale stacks and previously saved plans can still show longer sale times. Repeating a suggested batch immediately would invalidate its demand allowance. Saved unsold commitments now reduce the allowance for additional crafting on the same sale world and quality, across source DCs and acquisition methods. NQ and HQ demand remains separate. Unrecorded inventory is not counted.

This is a demand-based planning estimate, not a promise: competition, listing price, stack size, changes in demand, and time spent unlisted affect actual sales. The saved monitor applies the latest qualifying local rate (or explicitly labeled saved data) to the original saved batch quantity.

## API access

The integrated Universalis reads and optional Saddlebag Exchange history queries use public endpoints, with **no API key or account required**. Requests go directly from the plugin to those services. Universalis requests are paced at least one second apart with bounded retries and aggregate caching; Saddlebag requests are on demand, paced 30 seconds apart, and cached for 15 minutes. There is no data-upload integration. Saddlebag receives item, home-world, quality, and history-window fields, not your checklist or notes. Public access does not guarantee provider availability or complete market coverage.

## Additional regional history

Expand **Additional regional history — Saddlebag Exchange** under an opportunity and press **Load regional history** for regional units/day, median price, and sampled transaction/quantity totals. This optional service derives its FFXIV market data from Universalis too; it adds regional analysis, not independent confirmation. Its metrics never replace home-world profit calculations or batch sale-time estimates.

Lookups are paced and cached, with explicit unavailable/error states. See [the data-source review](docs/data-sources.md) for the tested API contract, provider comparisons, provenance, and limitations.

## Screening and data availability

The aggregate API is preferred by Universalis for clients that do not yet need individual listings. It returns world/DC minimum listing prices, quality-specific average sale prices, daily sales velocity, and upload timestamps. Average sale prices and velocity are calculated by Universalis from the last four days. These are estimates from crowdsourced data, not guaranteed demand.

A preliminary crafting signal requires fresh aggregate costs for every ingredient. The screener compares unit-cost estimates, while final validation still requires whole-stack affordability, enough stock, and sampled sale evidence. A preliminary signal can disappear during validation; only validated opportunities are displayed.

Successful aggregate responses are cached in memory for 10 minutes by world/DC and item. Unavailable items are cached for two minutes. Pausing and resuming reuses recent batches; a plugin restart clears the cache. Missing or explicitly failed API items are recorded as unavailable, not as zero-price bargains. A network error leaves coverage partial. Stale data remains distinguishable from screened-but-unavailable data.

The first broad pass can take several minutes: all requests share the one-second pacing limit, and server latency/retries add time. Visiting another DC requires a second aggregate scope and roughly doubles the screening requests. Detailed verification adds requests after screening. The UI reports progress through both stages.

## How opportunities are estimated

**Selling price:** the lower of the quality-specific median from usable sampled sales in the last seven days and one gil below the lowest sampled home-world listing (minimum one gil). A recent home-world upload, a fresh competing listing, and sufficient sampled sales are required. Estimated revenue subtracts an assumed 5% selling tax, rounded down per unit.

**Resale:** compare each fresh sampled stack on another world in the current DC against selling all its units at home. The plan buys the entire stack and adds an assumed 5% purchase tax. A stack larger than the units observed in the recent sample is excluded. Only the best qualifying sampled stack per item/quality is displayed.

**Crafting:** compare alternate recipes and every batch size from 1–10 crafts. For each ingredient, find the lowest full-stack checkout cost covering the required quantity on a single source world, among the sampled fresh NQ listings. The recipe can source different ingredients from different worlds. The cost includes all purchased stacks plus assumed 5% purchase tax; leftover ingredients are valued at zero. Output quantity cannot exceed observed sampled units. Crafting output must also fit the configured sale horizon (default three days, adjustable 1–7), using the assumed sales share (default 25%). Among eligible batches, choose the highest estimated net gil per sale day, with a one-day minimum divisor and smaller quantities winning ties. This keeps a larger total profit from automatically outweighing a much longer sale time. Changing share or horizon clears results and requires recalculation.

All opportunity results require positive net profit, sufficient stock, complete cost data, and the configured budget/ROI/profit thresholds. The budget applies separately to each plan, not to a combined portfolio of plans. Profit assumes every output unit sells at the estimate. Travel, crafting time, gear, consumables, and recipe unlock costs are excluded. HQ craft results require you to produce HQ outputs; character recipe access and crafting ability are not validated.

Universalis returns up to 100 listings and 20 sales per item/quality/scope in these requests. The seven-day sales figure is the portion of that sample falling in the time window, **not total market volume or a sell-through prediction**. Requests are serialized, paced at least one second apart, batched by up to 50 IDs, and retried at most twice for throttling/server errors while respecting Retry-After. Shared ingredient responses are reused within a scan round. Large rounds can take several minutes.

The opportunity table uses fresh home-world, quality-specific aggregate units/day when available. If aggregate demand is missing or stale, it conservatively uses sampled units divided by the seven-day window. Explicit zero or invalid demand does not qualify for crafting. Regional volume is never substituted for home-world demand.

The public market is crowdsourced and can change between upload, scan, and purchase. Recheck the shopping plan before buying. Separate opportunities can depend on the same stock and must not be added together as guaranteed profit. If no plans qualify, the UI reports that honestly rather than filling the list with speculative margins.

## Manual comparison

The **Item lookup** view remains available for targeted investigation:

- Search by item name or exact ID; filter to craftable items.
- Save up to 50 watchlist items plus sale world, output quality, and comparison preferences.
- Choose public worlds grouped by region and data center.
- Compare historical output prices with local/DC NQ ingredient costs.
- Inspect source worlds, available quantities, and sale/upload/review ages.

Manual comparison uses the first matching recipe and partial-stack material values; its figures are exploratory and differ from the whole-stack opportunity plans. It does not include purchase tax, and stale data is shown with age labels rather than excluded. Changing its inputs cancels pending requests and clears old results.

## Build and checks

Prerequisites:

- .NET 10 SDK; the project uses `Dalamud.NET.Sdk/15.0.0`.
- XIVLauncher/Dalamud installed and run at least once, with compatible assemblies. Set `DALAMUD_HOME` for a custom assembly directory.

```powershell
dotnet build MarketMage.sln -c Debug -p:Platform=x64 -p:RestoreLockedMode=true
dotnet run --project MarketMage.Tests -c Release
```

The offline regression executable needs no game installation and exits nonzero on failure. It covers parsing, HQ/NQ separation, whole-stack purchases, profit arithmetic, alternate recipes, budget/demand/freshness filters, full-catalog screening, separate availability counters, shortlist rotation, local-DC boundaries, incremental scanning, cancellation/resume, batching, caching, expiry, saved monitor/checklist roundtrips, sell-time assumptions, and external-provider error handling. CI runs these checks, builds on Windows, and uploads the DLL/manifest.

Optional read-only live API smoke check (not part of CI):

```powershell
dotnet run --project MarketMage.Tests -c Release -- --live-smoke
dotnet run --project MarketMage.Tests -c Release -- --regional-smoke
```

Debug DLL: `MarketMage/bin/x64/Debug/MarketMage.dll`.

## Load in game

1. Open `/xlsettings` → `Experimental`.
2. Add the full DLL path under Dev Plugin Locations.
3. Open `/xlplugins` → `Dev Tools` → `Installed Dev Plugins`.
4. Enable MarketMage and run `/marketmage` while logged in.
5. Check the detected sale world and source DC, set a budget if desired, and let the scan run.

### In-game acceptance checks

These require an actual FFXIV/Dalamud session; compilation, API smoke checks, and offline tests do not establish that they passed.

- Open with no watchlist and verify automatic discovery begins using the correct home world and current DC.
- Confirm catalog screening progresses beyond the old small subset to the full catalog, then detailed shortlist checks begin. Verify missing/stale data counters remain distinct from screened count.
- Confirm validated results arrive in batches and each row opens its shopping plan; check a resale stack and a multi-yield crafting recipe manually.
- Confirm HQ plans are labeled and show crafting job/level where applicable.
- Change budget/profit/freshness filters and verify old findings clear before rescanning.
- Pause, close, switch views, log out, travel to another DC, or unload mid-scan; verify no late results appear for the previous scope.
- Leave the view open for a full round and automatic rescan; verify obsolete findings disappear and expired findings are removed.
- Simulate connectivity failure and verify partial-result/error status plus delayed retry.
- Track an opportunity, mark purchases/crafting/listing progress, add notes, and reload the plugin; verify the saved snapshot and progress remain. Rescan a no-longer-qualifying item and confirm its saved plan is preserved with updated status.
- Change the assumed sales share and verify both opportunity and saved-batch times change consistently.
- Load optional Saddlebag context, switch items during loading, and test its unavailable state without disrupting the local scan.
- Check all columns and shopping details at different window sizes.
- Verify manual comparisons and saved watchlists/preferences still work after a plugin reload.

## Current boundaries

- Discovery covers crafting and market resale. Gathering, vendor arbitrage, desynthesis, ventures, and other acquisition methods are not modeled.
- No recursive subcraft costing, inventory-aware spending, travel optimization, character skill/recipe-unlock validation, or global search across every region.
- Whole-stack optimization uses the sampled listings, with one world per ingredient; it is not an exhaustive market or multi-world basket optimizer.
- No automatic trades or guaranteed profits, external alerts, persistent price database, or official plugin repository submission setup.

## Data sources

- [Universalis API documentation](https://docs.universalis.app/) and its [v2 schema](https://docs.universalis.app/api/schema/v2): aggregate world/DC screening, sale history, and listings.
- Dalamud/Lumina: player world/DC, marketable items, recipes, crafting job/level, and HQ capability.

- Verify a slow-moving multi-yield recipe is withheld when one craft exceeds the horizon; changing share/horizon must clear and recompute recommendations.
- Install and update from the custom repository in a real Dalamud session; automated packaging checks do not verify in-game installation.

### v0.7 in-game verification

- Reserve four units against a six-unit allowance and verify only two additional units can qualify; test partial sales and completion.
- Compare batch costs and price-drop outcomes against the displayed shopping plan.
- Refresh one tracked item during a full scan, then change DC or close the window; verify no cancelled result updates the old scope.
- Reload the plugin and confirm quantity fields and comparisons persist.

## v0.8 interface

- **Discover / My plans / Item lookup** provide direct navigation. Saved plans remain accessible while logged out.
- Discover leads with spend, profit, quantity, ROI, estimated sale time, and units/day. Choose most profit, fastest sale, lowest cost, or highest ROI. Selection stays highlighted and empty filters offer a reset.
- Budget, sales-share assumptions, and demand limits live under **Budget & preferences**. Coverage counters and calculation details are expandable.
- Selected opportunities show **Buy → Craft/bring home → List** steps before optional batch comparisons, price scenarios, evidence, and regional history.
- **Watch item** saves without reserving demand; **Plan this batch** reserves its output. Both open the saved plan. Existing reservations are preserved. Quantities, checklist progress, and manual completion remain separate.
- My plans shows active plans and reserved units, expands the plan just saved, and asks before deleting saved progress.

In-game layout QA remains necessary: test at 900px and wider, at your UI scaling, with long item names, empty results, expanded comparisons, and several saved plans. Automated builds do not verify the rendered FFXIV interface.
