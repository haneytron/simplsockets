SIMPLSOCKETS 1.0.1
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

**NUGET:** http://www.nuget.org/packages/SimplSockets

**WEB:**   http://www.getdache.net

**EMAIL:** info@getdache.net


VERSION HISTORY
============================================


1.1.0
------------------


- Fixed multiple bugs in how SimplSocket class handled communication errors.

- Fixed bugs that could cause infinite client hang in the case that the server stopped responding.

- Fixed semaphore calculation bugs.

- Errors are now handled gracefully and correctly, with atomic exception raising via the passed in error handler event delegate.

- Slight performance improvements enabled by smoother connection error handling.

- Special thanks to Ruslan (https://github.com/ruzhovt) for discovering and documenting the source of these errors.


1.0.1
------------------


- Fixed logic error in BlockingQueue constructor where _queue wasn't actually assigned

- Fixed logic error in Pool where resetItemMethod was not always called when Popping an item

- Fixed atomicity of Error event so that it is raised exactly once on disconnection regardless of multithreaded use

- On error, communication methods now exit gracefully after Error event is raised (no bubbled exceptions)

- Exposed CurrentlyConnectedClients property on ISimplSocketServer

- Added XML comments to a few classes


1.0.0
------------------


- Initial release of SimplSockets

- Includes client and server methods


INSTALLATION INSTRUCTIONS
============================================


Simply add the assembly to your project!

// To create a client

var client = SimplSocket.CreateClient(...)

// To create a server

var server = SimplSocket.CreateServer(...)
