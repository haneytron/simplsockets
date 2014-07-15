SIMPLSOCKETS 1.2.1
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

**WEB:**   http://www.getdache.net

**EMAIL:** [info@getdache.net](mailto:info@getdache.net)

**NUGET:** [SimplSockets](http://www.nuget.org/packages/SimplSockets)


VERSION INFORMATION
============================================


1.2.1
------------------

- Very minor refactor of code

- Removed IDisposable from the SimplSocket implementation, leaving it only on the ISimplSocket interface.


INSTALLATION INSTRUCTIONS
============================================


Just include the DLL in your project ([NuGet](http://www.nuget.org/packages/SimplSockets)) and then create a SimplSocket!

To create a client or server:

`var clientOrServer = new SimplSocket(...)`
