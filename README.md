# Your Project Name

A starter template for the AI Engineering Buildcamp capstone. Replace this README with a description of your own project.

## The Problem

Describe the problem your project solves and who has it. One or two sentences.

## What It Does

Describe what the AI system does and a typical interaction. What does the user provide? What does the system return?

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
