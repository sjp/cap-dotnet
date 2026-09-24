;; A WASI program that tries to read each of its arguments as a path beneath the directory it
;; was given, descriptor 3, and prints what happened:
;;
;;   notes.txt: read "..."
;;   ../secret.txt: refused, errno 76
;;
;; It asks for nothing a real program would not: an open that follows links, as open() does
;; by default, and a read. What it can reach is decided entirely by the host.
(module
  (import "wasi_snapshot_preview1" "args_sizes_get" (func $args_sizes_get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "args_get" (func $args_get (param i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "path_open"
    (func $path_open (param i32 i32 i32 i32 i32 i64 i64 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_read" (func $fd_read (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))
  (import "wasi_snapshot_preview1" "fd_close" (func $fd_close (param i32) (result i32)))

  ;; 0      argc, and the size of the argument strings
  ;; 16     an I/O vector for reading, then the count read
  ;; 28     the descriptor path_open returns
  ;; 32     an I/O vector for writing, then the count written
  ;; 1024   pointers to the arguments
  ;; 4096   the text this program prints
  ;; 8192   what it reads
  ;; 16384  the arguments themselves
  (memory (export "memory") 2)
  (data (i32.const 4096) ": read \"")
  (data (i32.const 4112) "\"\n")
  (data (i32.const 4128) ": refused, errno ")
  (data (i32.const 4160) "\n")

  (func $print (param $address i32) (param $length i32)
    (i32.store (i32.const 32) (local.get $address))
    (i32.store (i32.const 36) (local.get $length))
    (drop (call $fd_write (i32.const 1) (i32.const 32) (i32.const 1) (i32.const 40))))

  (func $print_number (param $n i32)
    (if (i32.ge_u (local.get $n) (i32.const 10))
      (then (call $print_number (i32.div_u (local.get $n) (i32.const 10)))))
    (i32.store8 (i32.const 4200) (i32.add (i32.const 48) (i32.rem_u (local.get $n) (i32.const 10))))
    (call $print (i32.const 4200) (i32.const 1)))

  (func $length (param $string i32) (result i32)
    (local $end i32)
    (local.set $end (local.get $string))
    (block $done
      (loop $next
        (br_if $done (i32.eqz (i32.load8_u (local.get $end))))
        (local.set $end (i32.add (local.get $end) (i32.const 1)))
        (br $next)))
    (i32.sub (local.get $end) (local.get $string)))

  (func (export "_start")
    (local $count i32) (local $i i32) (local $path i32) (local $length i32) (local $errno i32) (local $fd i32)
    (drop (call $args_sizes_get (i32.const 0) (i32.const 4)))
    (local.set $count (i32.load (i32.const 0)))
    (drop (call $args_get (i32.const 1024) (i32.const 16384)))

    ;; The first argument is the program's name.
    (local.set $i (i32.const 1))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $count)))
        (local.set $path (i32.load (i32.add (i32.const 1024) (i32.shl (local.get $i) (i32.const 2)))))
        (local.set $length (call $length (local.get $path)))
        (call $print (local.get $path) (local.get $length))

        ;; Follow links (lookup flag 1), open for reading (the fd_read right, 2).
        (local.set $errno
          (call $path_open (i32.const 3) (i32.const 1) (local.get $path) (local.get $length)
            (i32.const 0) (i64.const 2) (i64.const 0) (i32.const 0) (i32.const 28)))
        (if (i32.eqz (local.get $errno))
          (then
            (local.set $fd (i32.load (i32.const 28)))
            (i32.store (i32.const 16) (i32.const 8192))
            (i32.store (i32.const 20) (i32.const 256))
            (local.set $errno (call $fd_read (local.get $fd) (i32.const 16) (i32.const 1) (i32.const 24)))
            (drop (call $fd_close (local.get $fd)))))

        (if (i32.eqz (local.get $errno))
          (then
            (call $print (i32.const 4096) (i32.const 8))
            (call $print (i32.const 8192) (i32.load (i32.const 24)))
            (call $print (i32.const 4112) (i32.const 2)))
          (else
            (call $print (i32.const 4128) (i32.const 17))
            (call $print_number (local.get $errno))
            (call $print (i32.const 4160) (i32.const 1))))

        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next))))
)
