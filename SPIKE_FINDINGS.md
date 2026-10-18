# Single-file investigative spike

## Scope and artifacts

Inspected the October 1, 2026 English dump shard `p10p1113635`, its 7,693 selected
articles, and an independent rescan of all 768,512 source pages. This is an
investigative spike, not the production extractor or a finalized schema. The
original candidate files remain unchanged. No images were downloaded.

Generated artifacts under `results/enwiki-2026-10-01-p10p1113635/`:

| Artifact | Purpose |
| --- | --- |
| `taxobox-spike.ndjson.gz` | 7,695 per-box records with raw parameters, source offsets, section context, and provisional identity components |
| `taxobox-spike-report.json` | Context, identity, scalar-field, and exception statistics |
| `selection-audit.ndjson.gz` | Original wikitext of 62 additional review pages |
| `selection-audit-report.json` | Independent selection probe using Taxonbar and taxobox-like names |
| `parser-comparison.json` | Parsing-mode comparison for all candidates and review pages |
| `normalization-audit.json` | Name-resolution warning counts and examples |
| `text-spike-examples.json` | Illustrations of text lost by naive markup stripping |

Reproduce the core probes with:

```sh
.venv/bin/python -u spike_taxoboxes.py
.venv/bin/python -u spike_selection_audit.py
.venv/bin/python -u spike_parser_comparison.py
```

These scripts have pinned input/output paths and exploratory heuristics. Do not
treat them as general-purpose production commands or scientifically validated
name resolution. The normalization audit and text examples were ad-hoc probes.

## 1. The initial filter has identifiable omissions

The independent probe found 7,669 main-namespace, non-redirect pages invoking
Taxonbar. Of these, 7,608 were in the original selection. Broader taxobox-like
signals added Algae, resulting in **62 additional review pages** in total.
Taxonbar is a useful complementary signal; it is not ground truth or a measure
of exhaustive recall.

The 62 review pages break down as follows:

| Category | Pages | Examples |
| --- | ---: | --- |
| Known taxobox recovered with parser/style or name handling | 6 | Haematomyzus, Hickatee, Ptyodactylus, Slender glass lizard, Jacaranda, Ocalea (beetle) |
| Additional alias `Automated taxobox` | 1 | Haptophyte |
| Cultivar infobox | 16 | Kohlrabi, Broccoli, Cabbage, Kale |
| Generic infobox using taxobox colour | 1 | Algae |
| Other Taxonbar review pages | 38 | Historical/synonymous taxa, informal groups, disease pages, fossils, and disputed entities |

Five of the six recovered known boxes are missed by mwparserfromhell's default
handling of overlapping bold/italic syntax. `skip_style_tags=True` recovers all
five. Ocalea has an HTML comment inside the template name; removing comments
from the name before matching recovers it. Haptophyte uses `Automated taxobox`,
an alias whose redirect page is outside this shard; its live Wikipedia redirect
to Automatic taxobox was checked separately.

Comparison across the original 7,693 selected articles found the same 7,695
taxoboxes with style skipping and template-name comment cleanup. This validates
box counts for this sample, not equivalence of every parsed node or value.

**Recommendation:** improve parser configuration and name normalization, index
aliases across the complete template dataset, and retain a complementary
Taxonbar review stream. The original extraction remains at 7,693 articles; the
seven recoverable omissions have not been silently merged into it.

The review set also demonstrates why indiscriminate inclusion would be wrong:
Nanobacterium discusses nonliving crystallizations; Kting voar is putative;
Herto Man concerns fossil specimens; and many plant names describe historical
classifications. Preserve these distinctions rather than silently treating all
of them as current accepted taxa.

References:
- [mwparserfromhell limitations and style-skipping workaround](https://mwparserfromhell.readthedocs.io/en/latest/limitations.html)
- [Automated taxobox redirects to Automatic taxobox](https://en.wikipedia.org/wiki/Template:Automated_taxobox)

## 2. Identity components are accessible; scientific names need care

The provisional identity probe produced these results across 7,695 boxes:

| Method | Boxes |
| --- | ---: |
| Explicit `taxon` | 4,483 |
| Genus + species | 2,974 |
| Genus + species + subspecies | 66 |
| Virus species | 55 |
| Genus + species + botanical variety | 6 |
| Legacy binomial/trinomial | 3 |
| Title fallback requiring validation | 1 |
| Not mapped to a single name by these rules | 107 |

This is **7,587 source-derived candidate names**, one provisional title fallback,
and 107 intentionally unresolved cases. It is not a name-accuracy score.

105 proposed names contain parentheses and were flagged for taxonomy-name
resolution. Examples include `Zea (plant)`, `Vandellia (fish)`, and
`Bubalus (Anoa)`. Some parentheses disambiguate Wikipedia lookup keys, while
others can express subgenera. Blindly concatenating genus and species or simply
deleting all parentheses is unsafe. Preserve taxonomy lookup keys separately
from display names and nomenclatural components.

The 107 unresolved cases include 88 paraphyletic/informal groups, 10 hybrid
boxes, four manual taxoboxes, three population boxes, one Speciesbox, and one
special infraspecies box. Many are not missing data: they model a different
biological concept. Hybrid boxes may provide father/mother or species1/species2;
population boxes add a population to a parent identity. El Tor explicitly uses
an infraspecies rank of strain. Botanical `varietas` differs from `subspecies`.

The single title fallback is Panthera. Common/display-name formatting should
not invalidate otherwise explicit scientific components: for example, a
citation template in the common name does not make genus/species unresolved.

## 3. Article subject and box subject are separate

- 7,683 boxes occur before the first section heading; 12 occur inside sections.
- The section boxes are in 12 articles. Trypanosomatida has a primary lead box
  plus a secondary bacterial endosymbiont box; 11 articles have only section boxes.
- Cause and Virology sections explain several disease/organism ambiguities.
  Norovirus is mixed coverage, illustrating why position alone is not a verdict.
- Three boxes are nested inside `stack` layout wrappers: Chives, Coffea, and
  Asparagus. These are valid lead organism boxes, not irrelevant embedded data.
- Einkorn has two lead boxes for wild and cultivated wheat.

**Recommendation:** one article can describe several biological entities. Keep
article records and box/entity records separate, including box position,
section path, wrapper context, and an explicit subject-review flag. Do not
filter solely on top-level template status or the first box in the article.

## 4. Scalar fields still carry semantics and dependencies

There are 133 distinct raw parameter names, 39 used once. Examples include
apparent typos (`image_capion`, `image_widt`, `range_map_captio n`) and case
variants (`Species`). Preserve raw keys; do not assume MediaWiki parameters are
case-insensitive or that spaces and underscores are universally interchangeable.

Conservation status is not a single universal enum. This shard includes IUCN,
TNC, CITES, NZTCS, EPBC, SANBI, COSEWIC, and ESA systems. Status and status-system
must be interpreted together, including numbered secondary status fields.

18 status values use `{{TNCStatus}}` instead of a literal code. Those values
require template/data resolution; stripping markup would silently lose them.

`extinct` may be `yes`, a year, or approximate prose such as `probably by 1834`.
It is not safely typed as a boolean or integer. Absence of an `extinct` parameter
does not establish that a taxon is extant; many fossil taxa inherit information
through the automated taxonomy system.

This shard still has no Template:Taxonomy definitions. Parent chains, displayed
ranks, disambiguated display names, and inherited extinction information cannot
be validated completely from this shard alone.

## 5. Plain-text stripping loses content

In Amphibian, `{{cvt|7.7|mm}}` supplies a measurement. A direct `strip_code()`
call removes it, leaving a phrase like "with a length of just ." Language and
transliteration templates also disappear. References can contribute citation
text unless removed deliberately.

**Recommendation:** keep wikitext authoritative in the spike. Decide faithful
prose extraction using rendered content or a separately evaluated rendering
strategy. Do not silently treat markup stripping as faithful article text.
Later rendered content must retain its own revision/provenance information;
live rendering should not be assumed to match the pinned export snapshot.

## What this justifies next

We have enough single-file evidence to design a more robust candidate filter
and a representation with separate articles, biological identities, taxoboxes,
and provenance. Implementing full taxonomy expansion or final subject decisions
now would go beyond this evidence. Obtain the taxonomy/template subset from the
complete import before validating those steps. Image downloads remain deferred.
