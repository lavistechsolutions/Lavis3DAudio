# Contributing to LAVIS 3D AUDIO

Thank you for your interest in contributing to **LAVIS 3D AUDIO**.

LAVIS 3D AUDIO is an open-source Windows audio player. Contributions such as bug fixes, audio-device compatibility improvements, documentation, accessibility improvements, and carefully tested features are welcome.

## Before you start

1. Read the project's [`LICENSE`](LICENSE) and [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
2. Search existing [issues](https://github.com/lavistechsolutions/Lavis3DAudio/issues) and pull requests to avoid duplicate work.
3. For a significant feature or architectural change, open an issue first to discuss the proposal.
4. Do not submit copyrighted audio, proprietary libraries, credentials, private user data, or other material you do not have permission to publish.

## Development setup

- Windows 10 or Windows 11, 64-bit.
- Visual Studio 2022 with the workload/components required for WPF desktop development, or a compatible .NET SDK.
- The .NET SDK version specified by the project's `.csproj` / repository documentation.
- Any native or managed dependencies required by the project.

### Build

1. Fork this repository.
2. Clone your fork.
3. Open `Lavis3DAudio.sln` or the project file in Visual Studio (use the solution filename currently present in the repository).
4. Restore NuGet packages.
5. Build the application using the **Release** configuration.
6. Run the application and test the changes on Windows.

If the repository's solution or project filenames change, use the current filenames shown in the repository.

## Coding guidelines

- Keep changes focused and explain why they are needed.
- Follow the existing C# and WPF conventions in the codebase.
- Prefer clear names and maintainable methods over unnecessary complexity.
- Avoid unrelated formatting changes.
- Handle audio-device changes, unavailable devices, and audio errors gracefully.
- Do not log passwords, tokens, personal information, or private audio content.
- Do not add dependencies without explaining their purpose, license, and maintenance status.
- Preserve required third-party copyright and license notices.
- Do not commit generated build output, local settings, secrets, or user-specific files.

## Testing expectations

Before opening a pull request:

- Confirm the project restores and builds successfully.
- Run the application on a supported Windows system.
- Test the affected feature, including relevant failure cases.
- For audio changes, test with the relevant playback device and, where practical, more than one device.
- Check that volume levels remain safe and that processing does not introduce avoidable clipping or distortion.
- Include the steps you used to test the change.
- If you cannot test a particular scenario, state that clearly.

## Pull request process

1. Create a branch with a descriptive name, such as `fix/audio-device-switch`.
2. Make small, logically grouped commits.
3. Update documentation and `CHANGELOG.md` when appropriate.
4. Open a pull request against the default branch.
5. Describe the problem, the solution, related issue numbers, and tests performed.
6. Include screenshots or a short recording for user-interface changes when useful. Do not include copyrighted media unless you have permission.

The maintainers may request changes, ask questions, or decline a contribution that is outside the project's scope.

## Licensing contributions

By submitting a contribution, you confirm that:

- You created it or have the right to submit it.
- You have the right to make it available under the project's applicable license.
- You understand that the project is distributed under the MIT License unless a file or component states otherwise.
- You have identified any third-party code or assets and supplied their applicable license and attribution information.

Unless you clearly state otherwise and the maintainers agree, contributions submitted for inclusion in the project are offered under the MIT License, consistent with the project's license.

## Community standards

Be respectful, constructive, and considerate. Harassment, discrimination, personal attacks, and disruptive behaviour are not acceptable. See [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) if the project has adopted one.

## Questions

For bugs and feature proposals, use the GitHub issue tracker:

https://github.com/lavistechsolutions/Lavis3DAudio/issues
