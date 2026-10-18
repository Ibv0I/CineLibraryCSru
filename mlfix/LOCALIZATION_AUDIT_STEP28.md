# Localization pass 28 — visible UI only

This pass fixes several remaining English literals that were assembled directly in code rather than routed through the localization helper:

- The bottom-bar Drives tooltip now localizes its label while preserving the live drive count.
- Fallback drive names in the drive-add flow now use the localized `Drive` label.
- The missing-file status chip on movie details now uses the existing localized `MISSING` text.

Hidden TV Shows/episode screens and TMDb dialogs remain out of scope, as requested. No resource entries were added in this pass; it reuses existing keys to avoid unnecessary PRI resource churn.

Validation: changed C# files checked for balanced targeted replacements; resource XML sets are unchanged from the PRI-fixed step 27 archive. Full C# build and Windows runtime test were not available in this environment.
