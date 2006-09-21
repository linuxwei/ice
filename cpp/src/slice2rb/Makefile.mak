# **********************************************************************
#
# Copyright (c) 2003-2006 ZeroC, Inc. All rights reserved.
#
# This copy of Ice is licensed to you under the terms described in the
# ICE_LICENSE file included in this distribution.
#
# **********************************************************************

top_srcdir	= ..\..

NAME		= $(top_srcdir)\bin\slice2rb.exe

TARGETS		= $(NAME)

OBJS		= Main.obj

SRCS		= $(OBJS:.obj=.cpp)

!include $(top_srcdir)/config/Make.rules.mak

CPPFLAGS	= -I. $(CPPFLAGS)

!if "$(BORLAND_HOME)" == "" & "$(OPTIMIZE)" != "yes"
PDBFLAGS        = /pdb:$(NAME:.exe=.pdb)
!endif

$(NAME): $(OBJS)
	$(LINK) $(LD_EXEFLAGS) $(PDBFLAGS) $(OBJS) $(PREOUT)$@ $(PRELIBS)slice$(LIBSUFFIX).lib $(BASELIBS)

clean::
	del /q $(NAME:.exe=.*)

install:: all
	copy $(NAME) $(install_bindir)

!if "$(BORLAND_HOME)" == "" & "$(OPTIMIZE)" != "yes"

install:: all
	copy $(NAME:.exe=.pdb) $(install_bindir)

!endif

!include .depend
