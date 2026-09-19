# Project collaboration rules

Apply `baseline` to engineering work and `technical-writing-review` to durable technical text. Apply `wayfinder` when working through the route map and consult the GitHub tracker instructions before claiming a ticket.

## Product and repository boundary

- Read `docs/PRODUCT_BASELINE.md` and `CONTEXT.md` before changing gameplay or experiment semantics.
- This is an independent Godot project. Unity is a reference, not a live dependency or a synchronized upstream.
- Confirmed direction: Windows/PC first, Godot .NET, independent C# core, a new save format for this demo.
- Preserve product goals and observable contracts. Old class names, serialization layout, scene hierarchy and editor tooling are not mandatory designs.
- Art may be reused or rebuilt. Record the source/version of reused material and validate imports in Godot.

## Implementation and verification

- Keep deterministic rules and experiment state outside Godot node types where practical. Use a small explicit host boundary rather than a speculative cross-engine framework.
- Use maintained project files; commit the files needed for a clean checkout to build. Generated caches and local SDK paths do not belong in version control.
- Keep secrets outside the repository. Fixed clients are the default for automated tests; document real-provider experiments and their budgets before running them.
- Report the exact build/test input, outcome and remaining limitations. A core test result does not prove scene behavior, visual acceptance or a working export.
- Prefer branches with the `codex/` prefix for implementation. Wayfinder research artifacts use their prescribed `research/<name>` branches.

## Work coordination

- GitHub Issues are the canonical source for map state, child tickets, claims and dependencies.
- Claim an unblocked ticket before working it. Refer to tickets by their linked titles.
- Keep decisions in resolution comments and link their evidence. The map contains only a one-line index of resolved decisions.
- Do not edit the Unity reference repository as part of this reconstruction.
