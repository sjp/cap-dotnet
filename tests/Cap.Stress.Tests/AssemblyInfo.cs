// One test at a time, for three reasons that each hold on their own. The backend under test is
// substituted process-wide, so two tests running together could each be running on the other's.
// The leak check counts the descriptors the process holds, which a neighbouring test would
// change. And the numbers each race reports are a rate of lost races, which depends on how much
// else the machine is doing at the time and means nothing measured beside another stress test.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
