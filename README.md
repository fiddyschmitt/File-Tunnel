# File Tunnel

Tunnel TCP connections through a file.

## Example 1 - RDP connection

You'd like to RDP from Host A to Host B, but a firewall is in the way. But both hosts have access to a shared folder.

### Host A
``ft.exe --tcp-listen 127.0.0.1:5000 --write "\\server\share\1.dat" --read "\\server\share\2.dat"``

### Host B
``ft.exe --read "\\server\share\1.dat" --tcp-connect 127.0.0.1:3389 --write "\\server\share\2.dat"``

Now on Host A, open the RDP client and connect to: ``127.0.0.1:5000``

