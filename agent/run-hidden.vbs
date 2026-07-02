' Windowless launcher for the Straylight agent (no console flash when run as the
' interactive user). wscript.exe has no console; window style 0 = hidden.
Set sh = CreateObject("WScript.Shell")
sh.Run "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\Users\user\Documents\Work\the child\agent\straylight-agent.ps1""", 0, False
