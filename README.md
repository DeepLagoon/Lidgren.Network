# Lidgren.Network (Space Wizards Edition) ![Build & Test](https://github.com/space-wizards/lidgren-network-gen3/workflows/Build%20&%20Test/badge.svg) [![NuGet link](https://img.shields.io/nuget/v/SpaceWizards.Lidgren.Network)](https://www.nuget.org/packages/SpaceWizards.Lidgren.Network)
Lidgren.Network is a networking library for .NET, which uses a single UDP socket to deliver a simple API for connecting a client to a server, reading and sending messages. It supports **.NET Standard 2.1+**.

**Space Wizards Edition** is a maintained fork of Lidgren.Network with improvements such as bugfixes, performance improvements (`Span<T>`!), new features, etc... See the [Release Notes](RELEASE-NOTES.md) for differences.

We're using `SpaceWizards.Lidgren.Network` as a networking layer for [Space Station 14](https://spacestation14.io) and [Robust](https://github.com/space-wizards/RobustToolbox).

## Contact

If you'd like to get in contact with us or have any questions, you have two options:
* You can ask in `#other-game-dev` on [our Discord](https://discord.ss14.io).
* Use [GitHub discussions](https://github.com/space-wizards/SpaceWizards.Lidgren.Network/discussions)

## DeepLagoon engine integration

The `wizards` branch is the source for the Lidgren submodule in
[DeepLagoon/RobustToolbox](https://github.com/DeepLagoon/RobustToolbox).
The engine always records an exact commit, so cloning a particular engine
revision remains reproducible.

The engine's [Update Lidgren pin workflow](https://github.com/DeepLagoon/RobustToolbox/actions/workflows/update-lidgren.yml)
checks this branch every 15 minutes and can also be started manually.
For a new commit it runs the standalone Lidgren tests and the engine networking
integration tests before committing the new submodule pin to engine `main`.
Failed validation leaves the existing pin in place. Rewinds or divergent
history require manual review. If either branch advances during validation,
the next run validates the latest commits.

This updates the engine source dependency only; it does not deploy a game server.
