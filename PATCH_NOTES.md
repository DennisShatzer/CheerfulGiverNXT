# CheerfulGiverNXT Patch Notes (Auto Token Refresh + SQL Storage)

## What this patch does
- Tokens are stored in SQL under `SecretKey = MACHINE:<COMPUTERNAME>` (from `Environment.MachineName`).
- Subscription key is stored once under `SecretKey = __GLOBAL__`.
- Every SKY API request automatically receives:
  - `Authorization: Bearer <access_token>`
  - `Bb-Api-Subscription-Key: <subscription_key>`
  via `BlackbaudAuthHandler`.

## One-time setup
1. Run `CGOAuthSecrets.sql` against your CheerfulGiver database.
2. Put your connection string + passphrase + Blackbaud keys in `App.config`.
3. In the app, click **Authorize this PC** once per operator computer.

## Backwards compatibility
- `GiftWindow` includes an obsolete 3-argument constructor so older call sites still compile.

## Patch v2
- Fixed App.config: added <configuration> root element to resolve "multiple root level elements" compiler error.
