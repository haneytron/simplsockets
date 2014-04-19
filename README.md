SIMPLSOCKETS 1.1.2
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

**NUGET:** http://www.nuget.org/packages/SimplSockets

**WEB:**   http://www.getdache.net

**EMAIL:** info@getdache.net


VERSION INFORMATION
============================================


1.1.2
------------------

- Optimizations to initial buffer allocations and sizes which result in a substantially smaller memory footprint

- Heuristics-based pool population (20% allocated initially instead of 100%, will grow as needed)

- 1/10 (or less) memory allocated for buffer collections when compared to previous versions.


INSTALLATION INSTRUCTIONS
============================================


Just include the DLL in your project ([NuGet](http://www.nuget.org/packages/SimplSockets)) and then create the appropriate client or server object!

To create a client:

`var client = SimplSocket.CreateClient(...)`

To create a server:

`var server = SimplSocket.CreateServer(...)`
