SIMPLSOCKETS 1.0.1
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

http://www.getdache.net

info@getdache.net


VERSION HISTORY
============================================


1.0.1
------------------


- [x] Fixed logic error in BlockingQueue constructor where _queue wasn't actually assigned

- [x] Fixed logic error in Pool where resetItemMethod was not always called when Popping an item

- [x] Fixed atomicity of Error event so that it is raised exactly once on disconnection regardless of multithreaded use

- [x] On error, communication methods now exit gracefully after Error event is raised (no bubbled exceptions)

- [x] Exposed CurrentlyConnectedClients property on ISimplSocketServer

- [x] Added XML comments to a few classes


1.0.0
------------------


- [x] Initial release of SimplSockets

- [x] Includes client and server methods


INSTALLATION INSTRUCTIONS
============================================


Simply add the assembly to your project!

// To create a client

var client = SimplSocket.CreateClient(...)

// To create a server

var server = SimplSocket.CreateServer(...)
