# ADR-0014: Shared images are private by default

Status: Accepted 2026-11-09 (PD-37, product owner). Addendum §5.1.3, §11.10. Contracts C1, C4. Completes ADR-0001.

## Context
ADR-0001 put the `#k=` link key on snippets and files by default and said only that images "may be public". Screenshots
often contain private data (chats, dashboards, documents), and a 30-bit short code is not a secret.

## Decision
- Images (and all media) are created with a link key, like snippets and files: `https://zaa.gl/<code>#k=<key>`.
- The owner may make an image public, at creation or later. A public image works without a key and may get an Open
  Graph preview.
- Making a keyed image public removes the key hash; making it private again issues a new key, so the old keyless URL
  stops working.
- Video and screen recordings: settled by PD-45 (ADR-0021), same rule as images.
  To confirm: whether a guest may choose public at creation (guests cannot change it later).

## Consequences
- Default image links do not unfurl in chat apps; owners who want a preview make the image public.
- `ContentLinkRequest.RequireLinkKey` defaults to `true` for every kind.

## Rejected options
- Public by default for images: exposes screenshots to code enumeration.
