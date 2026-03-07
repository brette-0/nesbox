NAME := test
SRC  := source.s
OBJ  := $(SRC:.s=.o)

CFG  := ld65.cfg
OUT  := $(NAME).prg

CA65 := .\ca65
LD65 := .\ld65

.PHONY: all clean

all: $(OUT)

%.o: %.s
	$(CA65) $< -g --cpu "6502x" -o $@

$(OUT): $(OBJ) $(CFG)
	$(LD65) --dbgfile "test.dbg" -C $(CFG) -o $(OUT) $(OBJ)

clean:
	powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Remove-Item -Force -ErrorAction SilentlyContinue -- '$(OBJ)','$(OUT)','$(MAP)'"
