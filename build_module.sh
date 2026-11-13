#!/bin/bash
set -e
# Compiles the DLL and packages Decisions.Zitac.S3.zip (needs the CreateDecisionsModule global tool).
dotnet msbuild build.proj -t:build_all
echo "Module built: Decisions.Zitac.S3.zip"
