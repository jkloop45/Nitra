@echo off
call %~dp0\sub-env.cmd

echo ---- DISABLED TESTS ----
set TeamCityArgs=
if defined TEAMCITY_VERSION set TeamCityArgs=-team-city-test-suite:Nitra_Disabled
set OutDir=%~dp0\Bin\%Configuration%\Disabled
set Tests=%~dp0\!Disabled

call %~dp0\sub-run.cmd

if not defined RunNopause pause