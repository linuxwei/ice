' **********************************************************************
'
' Copyright (c) 2003-2004 ZeroC, Inc. All rights reserved.
'
' This copy of Ice is licensed to you under the terms described in the
' ICE_LICENSE file included in this distribution.
'
' **********************************************************************

Module valueS

    Private Function run(ByVal args() As String, ByVal communicator As Ice.Communicator) As Integer
	Dim adapter As Ice.ObjectAdapter = communicator.createObjectAdapter("Value")
	Dim [object] As Ice.Object = New InitialI(adapter)
	adapter.add([object], Ice.Util.stringToIdentity("initial"))
	adapter.activate()
	communicator.waitForShutdown()
	Return 0
    End Function

    Sub Main(ByVal args() As String)
	Dim status As Integer = 0
	Dim communicator As Ice.Communicator = Nothing

	Try
	    Dim properties As Ice.Properties = Ice.Util.createProperties()
	    properties.load("config")
	    communicator = Ice.Util.initializeWithProperties(args, properties)
	    status = run(args, communicator)
	Catch ex As System.Exception
	    System.Console.Error.WriteLine(ex)
	    status = 1
	End Try

	If Not communicator Is Nothing Then
	    Try
		communicator.destroy()
	    Catch ex As System.Exception
		System.Console.Error.WriteLine(ex)
		status = 1
	    End Try
	End If

	System.Environment.Exit(status)
    End Sub

End Module
