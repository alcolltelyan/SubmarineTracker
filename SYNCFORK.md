1. Add the original repo as upstream

First, go to the original repo on GitHub and copy its URL.

Then in your local repo:

git remote add upstream https://github.com/ORIGINAL_OWNER/REPO_NAME.git

Verify it worked:

git remote -v

You should see both:

origin → your fork
upstream → original repo


2. Fetch updates from upstream

This pulls the latest changes but doesn’t merge yet:

git fetch upstream


3. Merge into your branch

If you’re working on main:

git checkout main
git merge upstream/main

If the original repo uses master:

git merge upstream/master


4. Push the updated fork to GitHub
git push origin main

Now your fork is up to date.