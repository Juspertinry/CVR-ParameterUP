# What is this?

ParameterUP is a mod which allows for an upped parameter rate via the ModNetwork in CVR.
Parameters can be synced up to 30hz, rather than the current 10hz limit the official server provides.
All users who want to send an enhanced update rate must have the mod installed on their client, and users
viewed without the mod still receive a client side smoothing pass up to 45hz (configurable through melonprefs) through interpolation.


## Installation

The mod requires [MelonLoader](https://github.com/lavagang/melonloader) (and optionally [BTKUILib](https://github.com/BTK-Development/BTKUILib) for a debugging tab).
Download the latest release DLL and place it into your Mods folder next to your ChilloutVR executable.


## Features
- **Mod Network transport** Everything you adjust, including face tracking, is sent over the
  Mod Network and allows for higher rates than the official server.
- **Interpolation for all** Data received from all users is interpolated, modded or unmodded.  
- **Server-free design** No data is sent outside of the game, only relying on the official Mod Network.
- **Adaptive rate** During high network congestion or missed data, the mod will request a lowered rate
  from the sender to avoid lost or inconsistent data.


## How to use

It requires very minimal setup, configure any settings such as your send rate or interpolation preference
from the MelonLoader settings.


## Building

Copy reference assemblies into `libs/` first. Then:

```
dotnet build src/ParameterUP.csproj -c Release
```

## NOTICE

This modification is not endorsed, affiliated, or approved by the ChilloutVR Team in any manner.
You are responsible for any action taken against you during the use of this modification.
