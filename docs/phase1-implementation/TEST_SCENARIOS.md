# Phase 1 manual test scenarios

Open http://localhost:3000. Sign in with your account. Mail goes to Mailpit if you start it (`docker compose --profile dev up -d mailpit`, http://localhost:8025).
Needs the key fix first (`/health/issuer` must say healthy, not KeyMismatch).

## A. Snippets
1. Go to `/paste/new`, paste some code, pick a language, create. Expect a zaa.gl link.
2. Open the link in a private window. Expect highlighted code, no sign-in needed.
3. Create a Markdown snippet with `<script>alert(1)</script>` and a `[x](javascript:alert(1))` link. Expect no alert, link inert.
4. Create with a password. Private window asks for it. Wrong password is refused, right one opens.
5. Create with a short expiry. After it passes, the link shows "expired".
6. Edit the snippet twice. Owner sees versions and a diff.
7. Switch the site to Arabic. Layout is right-to-left, text readable.
8. Burn-after-read snippet: first view works, second shows "burned".

## B. Images
1. `/files`: upload a PNG, JPEG, GIF and WebP. Each shows a thumbnail.
2. Open `/i/<slug>`. Try background switcher and zoom.
3. Download it and compare SHA-256 with the original (`certutil -hashfile file SHA256`). Must match.
4. Upload a text file renamed `.png`. Expect it treated as a file, not an image.
5. Upload the EICAR test string as `eicar.txt`. Expect it never served.

## C. Files and burn
1. Upload a 50 MB file. Pause and resume works.
2. Create a burn-after-download link. Download once, then reload. Second attempt shows "gone".
3. Open the same burn link in two tabs and click download at the same time. Only one succeeds.
4. Set an expiry, wait, confirm the file is gone.
5. Upload `.exe` as guest or Free. Expect refusal. Upload an encrypted ZIP. Expect refusal.
6. Check the quota meter goes up on upload and down on delete.
7. The file page shows SHA-256 with a copy button.

## D. CLI
1. `zaagl login` and approve at `/activate` with the code.
2. `zaagl whoami`.
3. `echo hello | zaagl paste` returns a working link.
4. `zaagl link https://example.com`, `zaagl ls`, `zaagl logout`.

## E. Account
1. Forgot password, then reset it from the email link.

## F. Security checks (pen-test)
1. Snippet and Markdown XSS payloads (A3). Console must show no CSP violations.
2. Guess slugs and private keys: wrong `#k=` must never reveal content.
3. Download a burned or expired file by its old ticket URL. Must fail.
4. Upload a decompression bomb image and a huge-pixel PNG. Must be rejected, server stays up.
5. 31 guest snippets in an hour. The 31st is refused.
6. Open another user's owner page or delete URL. Must be 403/404.
7. Revoke a CLI session from settings. The CLI token stops working.
