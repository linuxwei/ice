// **********************************************************************
//
// Copyright (c) 2001
// MutableRealms, Inc.
// Huntsville, AL, USA
//
// All Rights Reserved
//
// **********************************************************************

#include <Ice/Ice.h>
#include <TestI.h>

using namespace std;

int
run(int argc, char* argv[], const Ice::CommunicatorPtr& communicator)
{
    string endpts("tcp -p 12345 -t 2000");
    Ice::ObjectAdapterPtr adapter = communicator->createObjectAdapterWithEndpoints("TestAdapter", endpts);
    Ice::ObjectPtr d = new DI;
    adapter->add(d, "d");
    d->_addFacet(d, "facetABCD");
    d->_addFacet(new FI, "facetEF");
    d->_addFacet(new GI(communicator), "facetG");

    adapter->activate();
    communicator->waitForShutdown();

    d->_removeAllFacets(); // Break cyclic dependencies
    return EXIT_SUCCESS;
}

int
main(int argc, char* argv[])
{
    int status;
    Ice::CommunicatorPtr communicator;

    try
    {
	communicator = Ice::initialize(argc, argv);
	status = run(argc, argv, communicator);
    }
    catch(const Ice::Exception& ex)
    {
	cerr << ex << endl;
	status = EXIT_FAILURE;
    }

    if (communicator)
    {
	try
	{
	    communicator->destroy();
	}
	catch(const Ice::Exception& ex)
	{
	    cerr << ex << endl;
	    status = EXIT_FAILURE;
	}
    }

    return status;
}
