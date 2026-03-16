Link
----

The link class is used to link API interface implementing classes to the Emulator.

``Link.Subscribe.OnTick(API.IClockDriven ctx)``
~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~

Components that should do work over time should extend ``API.IClockDriven``, a behavior that extends this will contain
an ``OnTick`` method that takes no arguments but has access to the ``System.virtualTime`` variable which is used to
measure how much work should be done at this given interval. Clock Driven Implementations complete *before* any System
action. This may be used to complete work for the beginning of a PPU cycle,