# File Tunnel

Tunnel TCP connections through a file.

<br />

## Download
Portable executables for Windows, Linux and Mac can be found over in the [releases](https://github.com/fiddyschmitt/File-Tunnel/releases/latest) section.

<br />

## Example 1 - Bypassing a firewall

You'd like to connect from Host A to Host B, but a firewall is in the way. But both hosts have access to a shared folder.

![ft_fw](img/ft_fw.png?raw=true "Bypass")

### Host A
``ft.exe -L 5000:127.0.0.1:3389 --write "\\server\share\1.dat" --read "\\server\share\2.dat"``

This command listens for connections on port 5000. When one is received, it is forwarded through the file tunnel and then onto 127.0.0.1:3389.

### Host B
``ft.exe --read "\\server\share\1.dat" --write "\\server\share\2.dat"``

Now on Host A, connect the client to ``127.0.0.1:5000`` and it will be forwarded to the remote server.

<br />
<br />

This is what the File Tunnel looks like when operating:

<br />

![Screenshot](img/ft_rdp_screenshot.PNG?raw=true "Screenshot")

<br />
<br />
<br />

## Example 2 - Tunnel TCP through RDP (similar to SSH tunnel)

You'd like to connect to a remote service (eg. `192.168.1.50:8888`), but only have access to Host B using RDP.

### Host A
``ft.exe -L 5000:192.168.1.50:8888 --write "C:\Temp\1.dat" --read "C:\Temp\2.dat"``

Run an RDP client and ensure local drives are shared as shown [here](https://github.com/fiddyschmitt/file_tunnel/assets/15338956/eb890310-47f5-4b46-9f74-471ec1735450).

RDP to Host B.

### Host B
``ft.exe --read "\\tsclient\c\Temp\1.dat" --write "\\tsclient\c\Temp\2.dat"``

Now on Host A, you can connect to `127.0.0.1:5000` and it will be forwarded to `192.168.1.50:8888`

<br />
<br />
<br />

## Other interesting features

* `-L` can be used multiple times, to forward numerous ports through the one tunnel.

* To enable other computers to use the tunnel, specify a binding address of `0.0.0.0`. For example: `-L 0.0.0.0:5000:192.168.1.50:3389` allows any computer on the network to connect to the tunnel and onto 192.168.1.50:3389
	
* Use `-R` for remote forwarding. For example: `-R 5000:10.0.0.50:6000` instructs the _remote_ side to listen on port 5000, and when a connection is received forward it through the tunnel and onto 10.0.0.50:6000 via the local machine. This allows you to share a server running on your local machine, with other computers.
	
* The read and write files don't have to be in the same folder or even server.

<br />
<br />
<br />

## How does it work?
The program starts a TCP listener, and when a connection is received it writes the TCP data into a file. This same file is read by the counterpart program, which establishes a TCP connection and onforwards the TCP data.
To avoid the shared file growing indefinitely it is purged whenever it gets larger than 10 MB.
