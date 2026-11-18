SaveFileSync repository
=======================

This folder is managed by SaveFileSync. Do not edit, rename or delete anything
inside it by hand: other PCs rely on its exact contents.

What is in here
---------------
  repo.json            format version of this repository
  devices\             one file per PC that uses this repository
  units\<id>\def\      definition of each synced game (name, locations)
  units\<id>\versions\ one JSON manifest per saved version: list of files
                       with their relative path, size and SHA-256 hash
  units\<id>\heads\    which version each PC currently has
  blobs\xx\<sha256>    the file contents, named by their SHA-256 hash

Getting your saves back without the app's sync engine
-----------------------------------------------------
SaveFileSync.exe can rebuild any version into a normal folder. It only reads
this repository and does not need any other configuration:

  SaveFileSync.exe list   --repo "<this folder>"
  SaveFileSync.exe export "<game name or unit id>" "<target folder>" --repo "<this folder>"
  SaveFileSync.exe export "<game name or unit id>" "<target folder>" --version <id> --repo "<this folder>"

From cmd.exe use:  start /wait SaveFileSync.exe export ...
(PowerShell works directly.)

Without SaveFileSync.exe at all: open the version manifest you want in
units\<id>\versions\, and for every entry of "files" copy
blobs\<first two characters of sha256>\<sha256> to "<path>" inside the folder
of its location ("loc"). docs/repository-format.md in the project repository
contains a PowerShell script that does exactly this.
