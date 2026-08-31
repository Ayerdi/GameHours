# GameHours release notes

Every installable release produced by `.github/workflows/package-windows.yml` must have reviewed Markdown notes committed here before the workflow is dispatched.

Use the exact SemVer as the filename:

```text
release-notes/0.2.0-beta.1.md
release-notes/0.2.0.md
```

Keep notes user-facing and concise. Describe observable changes, important fixes, migrations or known limitations; do not paste internal implementation logs, credentials, machine paths or private diagnostic data.

The release workflow refuses an empty/missing file and passes the same Markdown to Velopack and to the packaged `release-notes.md`, so the update card and post-update `Novedades` describe the exact installed version.
