Runtime CLI
-----------

Audio
=====
``--volume <float>``

This instruction sets the audio volume of the emulators output, its normalized between 1 and 0.

CPU
====
``--throttle <float>``

This instructions provides a coefficient to the default emulated clockspeed, permitting faster or slower execution.
Note that throttling of a speed different to ``1f`` whill mute all audio.

Tools
=====
``--pause``
This will pause emulation as soon as possible.

``--resume``
This will resume emulation as soon as possible.

``--quitDebugger``
This will unbind the debugger, permitting emulation without tool integration.