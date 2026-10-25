# Shipped sounds: where they come from

Every file in this folder is made by `scripts/sounds/make-sounds.cs` (#306): marimba, kalimba and bell notes from
decaying partials, wooden knocks from filtered noise, plucks with the Karplus–Strong algorithm, and the old chime's two
tones. Nothing is recorded or taken from elsewhere.

**Licence:** CC0 1.0 Universal (public domain dedication), https://creativecommons.org/publicdomain/zero/1.0/. No credit is
needed in the app.

**Remaking them:** from the repository root, `dotnet run scripts/sounds/make-sounds.cs`. The script seeds its noise, so a
run without changes writes the same files.
