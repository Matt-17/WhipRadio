# Fonts and Web Asset Licenses

Last reviewed: 2026-06-21

Sources used:

- `src/WhipRadio.Web/Components/App.razor`
- `src/WhipRadio.Web/wwwroot/fonts/fonts.css`
- Google Fonts CSS for the previously linked Anton, IBM Plex Mono, and IBM Plex Sans families
- Google Fonts and upstream project license metadata

The web app serves its UI fonts locally from `src/WhipRadio.Web/wwwroot/fonts`.
Do not add browser-facing CDN or third-party stylesheet links without recording
the privacy and license impact here.

## Local Font Files

| Asset | Version/source path | License | Runtime network behavior |
| --- | --- | --- | --- |
| `Anton` regular | Google Fonts `anton/v27` | SIL Open Font License 1.1 | Served locally from `/fonts/anton-v27-regular.ttf` |
| `IBM Plex Mono` regular, medium, semibold | Google Fonts `ibmplexmono/v20` | SIL Open Font License 1.1 | Served locally from `/fonts/ibm-plex-mono-v20-*.ttf` |
| `IBM Plex Sans` regular, medium, semibold | Google Fonts `ibmplexsans/v23` | SIL Open Font License 1.1 | Served locally from `/fonts/ibm-plex-sans-v23-*.ttf` |

## License Notes

- SIL OFL permits bundling and redistribution with software, subject to the
  license terms.
- The font names may be reserved by their upstream projects. Do not modify and
  redistribute altered font binaries under the same reserved names.
- Keep font files and `fonts.css` in sync. The application head should not link
  to `fonts.googleapis.com`, `fonts.gstatic.com`, or any other remote font CDN.
