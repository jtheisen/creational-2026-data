# Control API and image priority design

## Intent

Keep the default image priority at zero while making every priority below one
ineligible for new metadata, source-copy, and resize claims. Provide HTTP
operations for future control-plane clients without coupling them to Blazor.

## Approach

Use ASP.NET Core controllers in the existing control app. Controllers call the
same coordinator, collection repositories, and SQL image-work repository as
the UI and CLI. This preserves the existing concurrency and attempt rules.
The alternative of a separate API service adds deployment and identity work;
minimal route handlers would provide the same behavior but the requested
controller-style surface is clearer as operations grow.

## Behavior

- The effective image-work threshold is `max(1, configured minimum priority)`.
  The configured minimum retains its focus function. Priority changes affect
  future claims; an already running attempt may finish.
- GET collection plan, run snapshots, concurrency, image settings, queue counts,
  and open resize batches. POST collection import, target staleness, concurrency,
  image settings, OTT subtree or image priority, and metadata or source staleness.
- Only the configured enwiki collection is accepted. Staleness requests must
  name a target in its plan. SQL image routes are available only when SQL image
  operations are enabled. Invalid inputs get 400, unknown resources get 404,
  and conflicting in-flight mutations get 409.
- The deployed App Service authentication protects `/api` like the dashboard.
  Mutating routes require `X-Creational-Control: 1` so cross-site forms cannot
  trigger work. No browser CORS policy is added; a future UI can be hosted at
  the same origin.

## Constraints

Keep the existing SQL and Blob storage layout. Reuse current durable state and
do not start background work from GET requests. Document API payloads and the
priority rule in the cutover runbook.
