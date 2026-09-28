"""Stateless pipeline logic: geometry, parameters, and the rules that decide
whether a job may run.

Nothing in this package may import tkinter or threading. The pipeline has to be
testable without a display and reusable from a worker thread, and both of those
properties are lost the moment a widget or a lock leaks in here. It is a rule
that can be checked by grep, which is the point.
"""
