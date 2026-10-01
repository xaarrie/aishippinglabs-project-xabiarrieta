# Project Memory Timeline

A time-aware workspace for understanding how a project changes through conversations, meetings, documents, objectives, and specifications.

## The Problem

Project knowledge is spread across meeting transcripts, uploaded documents, and successive versions of objectives and specifications. Teams need a reliable way to recover context over time, identify inconsistencies between lifecycle snapshots, and see how decisions and requirements evolved.

## What It Does

The intended system lets a user hold conversations about a project or topic while incrementally adding meeting transcripts and other documents. Retrieval-augmented generation (RAG) makes the uploaded materials available as conversation context. Lifecycle snapshots provide points-in-time views that can detect inconsistencies, surface them for completion, and help trace how objectives and specifications change. This starter describes the product direction; it does not claim these capabilities are implemented yet.

## Setup

1. Install uv if you don't have it yet: https://docs.astral.sh/uv/getting-started/installation/

2. Clone this repository (or download the zip and extract it).

3. Create a `.env` file from the template and add your API key:

       cp .env.example .env

4. Install dependencies:

       uv sync

5. Start Jupyter:

       uv run jupyter notebook

## Notebooks

- `notebooks/01-setup.ipynb` - smoke test that confirms your environment works
- `notebooks/02-rag.ipynb` - a minimal RAG baseline you can adapt to your own data

## Data

Put your project data in the `data/` folder. See `notebooks/02-rag.ipynb` for how to load it.
