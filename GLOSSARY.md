# Glossary

## Taxobox alias seed

The mapping of recognized taxobox template names to their canonical template
families, supplied to the candidate filter before it scans a dump. It includes
the canonical names and template redirects: for example, `Infobox species` maps
to `Speciesbox`, and `Taxobox auto` maps to `Automatic taxobox`. This is a list
of **template names**, not biological taxa.

For the October 2026 dump, the seed came from a redirect index covering all 19
shards and contains 25 names. Knowing the names in advance lets each shard be
filtered in one pass, independently of the others. The worker collects redirects
again during that pass and checks that the resulting mapping matches the seed
before marking the complete dump processed. If the mapping differs, the pool
needs rebuilding with an updated seed so pages using a new alias are not missed.
