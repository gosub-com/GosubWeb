# Gosub Web Server #

This is a little web server I'm writing for fun.  [Gosub.com](https://gosub.com/)
is hosted using this web server on a droplet at 
[Digital Ocean](https://www.digitalocean.com/products/droplets)
for $6 per month.  The original version was written for
[OpenViewtop](https://github.com/gosub-com/OpenViewtop). It was before
[Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-6.0)
was available, and I was having a lot of problems getting WebSockets to work
with HTTP.SYS.  So I used the [RFC's](https://www.rfc-editor.org/rfc/rfc2616)
to write my own HTTP/WebSockets server.

This is a toy, just for fun.  There is no support. 

## Installation

Go to [Digital Ocean](https://www.digitalocean.com/products/droplets) and 
setup an Ubuntu server with a static IP address.  Point your domain to the
server.

From Visual Studio, open the *GosubWeb* solution, then select the *WebSite*
project, and publish it with *Build...Publish...*.  Make sure the
*Target Runtime* matches the server archtecture.  Use `lscpu` on the
server, so if it says `Architecture: x86_64`, publish to `linux-x64`.

Copy the files onto the server.  From Windows, you can use the command line
`scp -r * root@your-domain.com:~/.`.   From the server, make the file
executable with `chmod +x WebSite`, then run it with `sudo ./WebSite`.

In the console, you should see a log entry at the top, like:

	2022-10-23, 20:01:00.022,  INFO: *** STARTING LOG *** [C:\Users\Jeremy\Documents\git\GosubWeb\WebServer\Log.cs: 43, .cctor]

You should then be able to browse to your domain and see a starter web site.
Copy your web site files into the `www` directory located in the executable
folder, and they will be shown.

To leave it running after you exit SSH, use this command:

	nohup sudo ./WebSite &

## Secure it with HTTPS

Using Let's encrypt, you can setup the server to use HTTPS for free.
First install `certbot` on the server - use Google to figure out how
to do that, since it's been a while since I did it.  Make sure the
server is running and serving pages to the browser, then, from the
web site root (i.e. `cd www`), do the following:

	sudo certbot certonly --webroot -d your-domain.com
	cp /etc/letsencrypt/live/your-domain.com/fullchain.pem ../fullchain.pem
	cp /etc/letsencrypt/live/your-domain.com/privkey.pem ../privatekey.pem

Note that you will need to look at the output of the `certbot` command and
to find the exact URL to use, (e.g. it could be 
`/etc/letsencrypt/live/your-domain.com-0001/fullchain.pem`, etc)

Restart the web server for the certificate to take effect.  Look at the
console output to make sure there wasn't an error setting up the HTTPS
and certificate.  If it is working, the browser will automatically be
redirected to HTTPS when you reload the page.


