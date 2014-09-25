SIMPLSOCKETS 1.3.0
===========


A spinoff library of Dache that provides highly efficient, scalable, simple socket communication.

**WEB:**   http://www.dache.io

**EMAIL:** [info@getdache.net](mailto:info@dache.io)

**NUGET:** [SimplSockets](http://www.nuget.org/packages/SimplSockets)


VERSION INFORMATION
============================================


1.3.0
------------------

- **BREAKING CHANGE**: `SimplSocket` has been divided into `SimplSocketClient` and `SimplSocketServer` to better reflect behaviour and use

- Fixed bugs related to asynchronous multiplexer state handling which could cause unhandled exceptions if a thread experienced an error while another thread was waiting for data

- Even more efficient communication via multiplexer data re-use and pools

- Code clean-up and optimization


INSTALLATION INSTRUCTIONS
============================================


Just include the DLL in your project ([NuGet](http://www.nuget.org/packages/SimplSockets)) and then create a SimplSocket!

To create a client or server:

`var client = new SimplSocketClient(...)`

`var server = new SimplSocketServer(...)`
