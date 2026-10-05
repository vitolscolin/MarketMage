# Market-data source review — 2026-10-05

Adding API providers helps only if they add useful information or resilience. Two services using the same upstream transactions are not independent confirmation, and their volumes must not be summed.

| Service | Verified role / provenance | MarketMage decision |
| --- | --- | --- |
| [Universalis](https://docs.universalis.app/) | Crowdsourced world/DC listings, sales history, aggregate prices and velocity. | Primary screening and final listing verification. |
| [Saddlebag Exchange](https://docs.saddlebagexchange.com/openapi.json) | Public history and market-analysis endpoints. The schema explicitly says its FFXIV history and marketshare endpoints call Universalis. Offers regional historical summaries beyond our short local transaction sample. | Added an optional, on-demand regional-history panel. Kept separate from local profits and batch sell-time estimates. |
| [XIVAPI v2](https://v2.xivapi.com/) | Searchable game sheets and patch-versioned item/game metadata. | Useful for future vendor/recipe enrichment or an out-of-game companion; Lumina already supplies local game sheets. This does not provide another independent set of current player-market transactions. |
| [Teamcraft](https://github.com/ffxiv-teamcraft/ffxiv-teamcraft/blob/staging/apps/client/src/app/core/api/universalis.service.ts) | Its source includes the Universalis client used for market-board pricing. Other Teamcraft features/data concern crafting and game metadata. | Potential future crafting/gathering enrichment; not added as a duplicate independent market feed. |
| [Oanor FFXIV Market API](https://www.oanor.com/api/ffxivmarket-api) | Its provider page identifies Universalis as the market-data source and requires an API key. | No integration: another wrapper would not improve independent market coverage. No account or subscription created. |

No additional independent public live FFXIV market-board feed was verified in this review. That is a research finding, not a claim that none exists.

## Saddlebag integration verification

The production API base is identified in the project's [client configuration](https://github.com/ff14-advanced-market-search/saddlebag-with-pockets/blob/master/app/requests/client/config.ts). The [history response types and current frontend request](https://github.com/ff14-advanced-market-search/saddlebag-with-pockets/blob/master/app/requests/FFXIV/GetHistory.ts) and [region-wide results UI](https://github.com/ff14-advanced-market-search/saddlebag-with-pockets/blob/master/app/components/FFXIVResults/item-history/Results.tsx) establish the meaning and scope of the summary fields.

MarketMage uses the documented `POST https://api.saddlebagexchange.com/api/ffxiv/v2/history` endpoint. Live production calls returned HTTP 200 for public item/world queries with:

```json
{
  "item_id": 3,
  "home_server": "Cactuar",
  "item_type": "nq_only",
  "initial_days": 7,
  "end_days": 0
}
```

Although the docs schema marks the last two fields deprecated, the tested production v2 endpoint required them. Missing them returned HTTP 400; the integration includes them. The frontend currently calls a v3 route, but this implementation uses the documented and live-tested v2 contract.

Only item ID, home-world name, quality, and time window are sent. Checklist notes, character identifiers, purchase progress, and saved plans are not sent. Responses are reduced to regional units/day, median price, sampled quantities/transactions, and retrieval time; buyer/retainer details are neither displayed nor persisted.

Requests happen only when the user presses **Load regional history**. They are paced 30 seconds apart and cached in memory for 15 minutes by item/world/quality. There are no automatic retries. Missing metrics, mismatched item IDs, HTTP errors, and service failures produce an unavailable state without changing local scans. Region-wide volume is never used as home-world demand or added to Universalis volume. History can be capped, and retrieval time does not establish the age of every underlying sale.
