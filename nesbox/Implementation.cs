namespace nesbox;

/*
 *  Hello user!
 *  For nesbox to use your cartridge, you must this file as on start it waits for cartridge to initialise
 *  SDL3 won't be running yet so it can take as long as you like, freezes or hangs shouldn't be my fault.
 *  If you like, you can rename 'Implementation' to anything, depending on your project.
 *  Thanks for choosing nesbox!
 */


public class Implementation : API.ICartridge {
    public void Initialize() { }

    public void ReadMemory() {
            
    }
        
    public void WriteMemory() {
            
    }
}