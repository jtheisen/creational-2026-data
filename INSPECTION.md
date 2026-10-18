# First candidate inspection

Source: `results/enwiki-2026-10-01-p10p1113635/candidates.ndjson.gz`.
This is a profile of one page-ID shard, not an estimate for all Wikipedia.
The reproducible exploration is `inspect_candidates.py`; detailed results are in
`results/enwiki-2026-10-01-p10p1113635/inspection.json`.

## Findings

- 7,693 articles contain 7,695 taxobox invocations. Two articles have two boxes:
  Einkorn describes wild and cultivated wheat; Trypanosomatida also contains a
  box for a bacterial endosymbiont. The data model must support multiple boxes
  per article and record their context rather than merging their parameters.
- 7,600 articles have a populated scientific identity parameter (`taxon`,
  `genus`, `species`, `binomial`, or `trinomial`). These are useful extraction
  signals, not proof of article subject or a complete classification.
- Identity forms vary: Aardvark supplies genus and species separately; Amphibian
  supplies `taxon=Amphibia`; Horse adds a subspecies; virus boxes can supply a
  full species name and a parent. Preserve these components before normalization.
- Common fields include authority, captions, synonyms, subdivisions,
  conservation status/system/references, range maps, and fossil ranges.
  They contain nested templates, links, citations, and lists. Avoid blanket
  string splitting or treating all values as plain text.
- This chunk's only retained dependency is `Template:Taxobox`. It has no
  `Template:Taxonomy/...` definitions. Full automatic classification cannot be
  reconstructed from this chunk alone.

## Images

- 7,406 articles (96.3%) have a populated recognized image or range-map field.
- Nonempty field occurrences: `image` 7,372; `image2` 1,013; `range_map` 2,367;
  `range_map2` 35; `range_map3` 1. These are field occurrences, not unique files.
- There are 10,469 distinct raw field values. They are not yet normalized or
  confirmed to identify downloadable image files.
- 187 populated image/map values contain templates, wikilinks, or line breaks.
  Amphibian and Cat use Multiple image collages; Bird uses CSS image crop;
  Actinopterygii uses an imagemap; Bison uses maplink referencing `.map` data.
  Jackal's `image2` contains an MP3. The field name does not guarantee an image.
- There are 33,687 explicit File/Image wikilink occurrences across 5,296 pages,
  plus galleries on 1,828 pages. Counts overlap and include repeated references;
  galleries and template-generated images require separate handling. This is
  not a complete or deduplicated inventory of article images.

Next extraction should give every media reference a role (taxobox illustration,
range map, body illustration, gallery, audio), preserve raw context, normalize
simple filenames, and flag complex values for specialized handling. Do not
download arbitrary raw image-field strings as filenames.

## Subject ambiguity

21 articles co-occur with medical, disease, or drug infoboxes. This is a review
signal rather than an exclusion rule:

- Rift Valley fever and Yellow fever describe diseases; the virus taxobox appears
  in a Cause/Virology section. Toxocariasis contains a box for its causative genus.
- Escherichia coli and Staphylococcus aureus are organism articles despite other
  medical/drug infoboxes. Mycoplasma is also clearly a genus article. Norovirus
  combines organism and infection coverage.

Three candidates have taxoboxes only inside other template invocations. More
generally, being a top-level template invocation does not mean the box is in
the article's lead: disease-section boxes can be top-level wikitext too.

Before final inclusion, record each box's section, source position, nesting,
scientific identity, and competing infoboxes. Preserve uncertain pages for
review rather than discarding them automatically.

## Suggested next pass

Produce one article record and separate taxobox/media-reference records keyed
back to it. Preserve raw values and normalized fields separately. Inspect lead
and section context for subject classification. Resolve taxonomy after obtaining
the relevant taxonomy templates from the other chunks. Evaluate rendered text
separately: mwparserfromhell's `strip_code()` alone loses template-rendered text
and can leave citation text in the prose.

No image files were downloaded and no raw extraction outputs were modified.
