SIMPLSOCKETS 1.3.1
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

**WEB:**   http://www.dache.io

**EMAIL:** [info@dache.io](mailto:info@dache.io)

**NUGET:** [SimplSockets](http://www.nuget.org/packages/SimplSockets)


VERSION INFORMATION
============================================


1.3.1
------------------


 - Keep-alive used to ensure proper disconnect when one side hangs up and TCP does not detect it


 - Much more robust communication via simplification of code


 - Fixed numerous communication bugs that could cause crashes


 - Better performance via better object pooling and re-use


INSTALLATION INSTRUCTIONS
============================================


Just include the DLL in your project ([NuGet](http://www.nuget.org/packages/SimplSockets)) and then create a SimplSocket!

To create a client or server:

`var client = new SimplSocketClient(...)`

`var server = new SimplSocketServer(...)`
