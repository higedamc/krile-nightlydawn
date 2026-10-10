# Render-frame assets (not part of the application)

This orphan branch exists only to host PNG frames so they can be embedded in pull request
descriptions. It is never merged into `master` and contains no code.

`pr16/` — frames produced by `tests/NightlyDawn.App.RenderTests` on the CI ubuntu runner
(`Avalonia.Headless` with real Skia rasterization, no display server), from
[PR #16](https://github.com/higedamc/krile-nightlydawn/pull/16),
workflow run [38030767713](https://github.com/higedamc/krile-nightlydawn/actions/runs/38030767713).

Each PR's own frames are uploaded by CI as the `mainwindow-render-frames` artifact; these
copies exist because artifacts cannot be embedded in Markdown.
