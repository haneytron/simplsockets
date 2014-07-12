SIMPLSOCKETS 1.2.0
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

**WEB:**   http://www.getdache.net

**EMAIL:** [info@getdache.net](mailto:info@getdache.net)

**NUGET:** [SimplSockets](http://www.nuget.org/packages/SimplSockets)


VERSION INFORMATION
============================================


1.2.0
------------------

- SUBSTANTIALLY optimized performance and memory usage. Much less memory used and buffer object creation. The result is much faster sockets!

- BREAKING CHANGES: altered interfaces and methods to be more OO-friendly and to enable client and server to have access to same operations

- Exposed events for MessageReceived and Error that you can hook into to receive (and process) messages, and to handle any communication errors

- Pool and Blocking Queue optimizations

- Refactored almost all code to be much more efficient


INSTALLATION INSTRUCTIONS
============================================


Just include the DLL in your project ([NuGet](http://www.nuget.org/packages/SimplSockets)) and then create a SimplSocket!

To create a client or server:

`var clientOrServer = new SimplSocket()`
