# Report templates

The two `.xlsx` files in this directory are deliberately generic examples:

- `enterprise-weekly-report-template.xlsx` is used by the enterprise-style exporter.
- `school-weekly-report-template.xlsx` is used by the school/internship exporter.

They contain placeholder identity fields and synthetic sample text. Replace the
placeholders in your local copy before exporting a real report. Keep personal or
organization-provided workbooks under `Templates/private/`; that directory is
ignored by Git and is never part of a public commit.

The exporter copies templates into the application data directory before writing
report rows, so the source workbooks remain unchanged.
