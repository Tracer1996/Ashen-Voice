# Ashen Voice 1.4.1 fixes

- Prevents multiple Ashen Voice instances from running at the same time.
- Uses unique temporary files and synchronized atomic writes for speaker state.
- Lets the overlay read state files while they are being replaced.
- Expands horizontal and vertical offset range to -10000 through 10000.
- Fixes elevated launch from the installer.
