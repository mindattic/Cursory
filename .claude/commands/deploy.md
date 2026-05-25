Deploy Cursory via **MindAttic.Deploy** (sibling repo at `D:\Projects\MindAttic\MindAttic.Deploy`). MindAttic.Deploy is the source of truth for every MindAttic deploy; this command shims into it.

The deploy fires the project's GitHub Actions workflow (`azure-deploy.yml`) by pushing `main`. The workflow then publishes `Cursory.Blazor` and lands it on the `cursory` Azure App Service slot at **https://cursory.azurewebsites.net** using `AZURE_WEBAPP_PUBLISH_PROFILE`.

Run this command and report the result:

```
powershell -NoProfile -ExecutionPolicy Bypass -Command "cd D:\Projects\MindAttic\MindAttic.Deploy; npm run deploy -- --app cursory"
```

It will:

1. Run the `dotnet-build` pre-deploy hook against `Cursory.Blazor.csproj` (`-c Release`) to catch compile errors locally before pushing.
2. `git -C ../Cursory push origin main` if local commits are ahead of remote, triggering the Actions workflow.
3. Print the Actions URL for monitoring: <https://github.com/mindattic/Cursory/actions/workflows/azure-deploy.yml>.

After running, summarize: which steps ran, what was pushed (or that there were no changes), and the Actions URL.

Notes:
- For a no-push rehearsal (build only, no push), append `--dry-run`: `npm run deploy -- --app cursory --dry-run`.
- App profile lives in `MindAttic.Deploy/projects.json` under `apps[]` slug `cursory`. To turn the deploy off temporarily, set `"disabled": true` there — the CLI will then skip it (and surface the `disabledNote` when applicable).
- The `cursory` App Service has WebSockets enabled (required for SignalR). If a deploy reports SignalR connect failures post-deploy, check `az webapp config show --name cursory --resource-group MyApps --query "webSocketsEnabled"`.
- Two seeded accounts ship in the deploy: `GunGreenEyes` and `GideonKain`, both with password `Happygirl1005`. They're idempotently seeded on every cold start.
