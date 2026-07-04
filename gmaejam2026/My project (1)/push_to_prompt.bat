@echo off
cd /d "%~dp0"
git remote remove prompt 2>nul
git remote add prompt https://github.com/safdsacvxzvxc/prompt.git
git push prompt main
git push prompt stable-checkpoint
echo.
echo ===== Done. Check messages above for errors. =====
pause
