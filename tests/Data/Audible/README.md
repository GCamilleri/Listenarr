# Audible catalogue fixtures

Captured live from `https://api.audible.com/1.0/catalog/products/` on 15/09/2026 with the same
headers and `response_groups` the production client sends
(`media,contributors,series,product_attrs,product_desc,product_extended_attrs,category_ladders`).

| File | Request | Result |
|---|---|---|
| `catalog-keywords-mistborn-the-final-empire.json` | `keywords=Mistborn, The Final Empire`, `products_sort_by=Relevance` | 4 products: `B07F88TSBT` (Mistborn: Secret History, 329 min), `8417347631` (Spanish edition, 1461 min), `B004SOK2SE` (Mistborn, 1499 min), `B0GFB7JV5P` (Briefly Summaries, 22 min) |
| `catalog-keywords-brandon-sanderson-mistborn-the-final-empire.json` | `keywords=Brandon Sanderson Mistborn The Final Empire`, `products_sort_by=Relevance` | 2 products, both Sanderson: `B07F88TSBT`, `B004SOK2SE` |
| `catalog-author-title-fields-brandon-sanderson-mistborn.json` | `author=Brandon Sanderson&title=Mistborn, The Final Empire` as separate fields | 0 products. This is why the separate-field search finds nothing |
| `catalog-author-brandon-sanderson-page2.json` | `author=Brandon Sanderson`, `num_results=50`, `page=1`, `products_sort_by=BestSellers` | `total_results` 202; `B004SOK2SE` appears on this second page. Only the first 8 products are kept so the fixture stays a sensible size; `total_results` is the real value |

The canonical Audible record for The Final Empire is `B004SOK2SE`, title `Mistborn`, subtitle
`Mistborn, Book 1`, series `The Mistborn Saga` position 1, runtime 1499 minutes. The words "Final
Empire" appear nowhere in it, which is why a substring filter on the detected title discards it.
