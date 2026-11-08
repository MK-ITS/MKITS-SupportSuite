# Remote support – how it works for you

Your supporter would like to look at your computer to help you. This page explains what happens and what you stay in
control of.

## Start

1. Your supporter gives you an **8-digit code**, usually on the phone.
2. Open the address your supporter names (e.g. `https://support.example.com`) and enter the code.
3. Download the support program and start it:
   - **Windows:** `MKITS-Support-….exe`. Windows may warn that the program is unknown (*More info → Run anyway*). No
     installation, no administrator rights needed.
   - **Linux:** the `.AppImage` file. Make it executable (right-click → *Properties → Allow executing*, or
     `chmod +x MKITS-Support-*.AppImage`) and start it.
4. The program shows the **name of your supporter** and the **address of the server**. Check both. Only if they are
   right, click **Allow**. Click **Deny** if anything seems wrong – nothing happens then.
5. On Linux with a modern desktop (Wayland), your system asks once more which screen you want to share.

## During the session

- The window of the support program stays open and shows who is connected. While it is open, your supporter sees your
  screen and – if you allowed it – uses mouse and keyboard.
- **Security code:** the window shows a short code. If your supporter asks, read it out; if it matches theirs, the
  connection is end-to-end encrypted between you and nobody else can watch.
- **Chat:** you can write messages to your supporter in the window.
- **Files:** when your supporter wants to send you a file, you are asked first; accepted files are saved in your
  *Downloads* folder. You can send files to your supporter with *Send file …*.
- **Administrator rights (Windows):** for some tasks your supporter may ask you to click **Restart with administrator
  rights** and to confirm the Windows prompt. Then your supporter can also handle Windows security prompts. This mode
  ends with the session.

## End

Click **End session** or simply close the window – the connection ends immediately. The program does not install
anything and does not stay on your computer; you can delete the downloaded file afterwards. To get help again later,
you need a new code.

## Installed for permanent support

Companies sometimes install the support agent permanently on their computers. Then nobody needs a code; depending on
your company's settings you are asked before a supporter connects, or you see a notice while someone is connected.
Questions about this go to your IT department.
