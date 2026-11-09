# ADR-0005: Burn After Downloading is claimed by the first GET of a button-minted ticket

Status: Accepted 2026-11-09 (PD-05). Addendum §8.2. Contract C2 (`IDownloadGrants`).

## Context
Chat apps and link scanners fetch URLs automatically; a burn that triggers on any fetch would destroy files before the
recipient sees them. Download managers resume with Range requests.

## Decision
- A download ticket is minted only by a POST from the download button (preview bots never POST). It expires 120 s after
  minting if unused.
- The burn is claimed atomically by the first GET of that ticket; exactly one request can win.
- The same grant may resume for 30 minutes, at most 16 requests; then the bytes are deleted.
- Previews, HEAD, viewer pages, descriptors and owner downloads never consume it. Burn files get no previews.
- Burn never depends on the User-Agent.

## Consequences
- Claim and grant live in one schema (`resources`) and one transaction; race test with concurrent first GETs.
- A download interrupted after the grace period is lost (documented).
- Applies to files. Open: burn-after-view for snippets, CAPTCHA on the burn POST, owner email on burn, which plans may
  use burn.

## Rejected options
- Consume at grant issue (~10 min): a failed first download would burn the file with no resume.
- Consume at a completed transfer: cannot be detected reliably behind Nginx; allows unlimited partial reads.
