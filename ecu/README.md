# ecu/ — put your SGBD files here

bmwdiag needs BMW SGBD files (`*.PRG`, `*.GRP`, e.g. `D72N47B0.PRG`, `KOMB87.PRG`, `D_MOTOR.GRP`,
`T_GRTB.PRG`) — the descriptions of each control unit. They are BMW property and are **not** part of this
repository; everything in this folder except this file is ignored by git.

Copy them here (flat, no subfolders), or point bmwdiag to another folder with `--ecu <dir>`,
the `BMWDIAG_ECU` environment variable, or `~/.config/bmw-mac-diag/config.json`.
