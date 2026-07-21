VAR iceking = "ICE!"

->start

/*
And another comment.
*/
=== function something(a,b) ===
#IF INKY && ( INK_TEST || INK_TEST ) 
well, this is ran from ink test.
#ELSE
{a}, {b}, and {iceking}!
#ENDIF
#IF HUH
#ELIF INK_TEST
Could this be another funky stuff?
#ELSE
#ENDIF
~return

=== start ===
// here we have a comment.
This is a convenience test file for InkTestBed.
~something(5,"goat")
the end.
->END

