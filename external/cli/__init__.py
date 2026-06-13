"""
Headless CLI for Casual Preloader.

Run with:  python -m cli <command> [options]

This package is the official machine integration surface (see
docs/preloader-integration-contract.md in the consuming repository).
It must never import PyQt6.
"""

from cli.main import main

__all__ = ["main"]
