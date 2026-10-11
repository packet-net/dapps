# Releasing DAPPS

How a change on master becomes a release that nodes install. `.github/workflows/release.yml` has the why of each step.

1. **Bump the version** in `src/dapps/Directory.Build.props`, and add an "In 0.x.y:" paragraph to `plan.md` saying what the release ships. Do both in one PR, either the last change of the release or a small release PR of its own.
2. **Merge it, then tag the merge commit:** `git tag v0.x.y && git push origin v0.x.y`. The tag must match the props file, or the release workflow's `version` job fails.
3. **The tag runs release.yml.** It tests, builds the binaries and the `.deb`s, creates the GitHub Release, and tells packet-net/apt to reindex.
   - If the test step fails three attempts running for reasons outside DAPPS, re-run the failed job (`gh run rerun <id> --failed`) rather than skipping tests.
   - The simulated-radio scenarios are left out of this gate while the comment in release.yml says so.
4. **Replace the generated release notes** with written ones (`gh release edit v0.x.y --notes-file ...`). Lead with what users notice, say whether every node has to upgrade together, and list known issues.
5. **Check apt** carries the new version: `curl -s https://packet-net.github.io/apt/Packages`, under `Package: dapps`.
6. **Re-pin pdn's app catalogue.** pdn's built-in list of installable apps pins DAPPS by version and checksum, in packet-net/packet.net `catalog/apps.yaml`.
   - Run `scripts/regen-catalog.sh --app dapps` there, open a PR (`catalog: dapps 0.x.y`, with `[skip-plan]`), and merge it. The next pdn node release then offers this DAPPS.
   - Skipping this step leaves pdn installing an old DAPPS: it sat at 0.34.3 through to 0.42.0.
