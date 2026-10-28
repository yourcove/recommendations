# Recommendations for Cove

Recommendations learn what you like from the way you already use Cove, then surface the content in your library you're most likely to enjoy. Nothing leaves your network. Everything is worked out from your own library and your own ratings.

## Installing

1. In Cove, open Extensions and search **Recommendations: Tastes**.
3. Requires Cove 1.7.0 or newer and the **AI Visual** extension, (ideally) run over your library.

Once installed, a **Recommended** page appears in Cove. Make sure to enable it in the settings -> Interface

## How it works

There are two halves to this.

**1. Figuring out what you like.** Cove watches how you actually use it: what you've watched and for how long, what you've liked and favorited, your ratings, the tags and performers in the content you keep coming back to, and more. From that it builds a picture of which content you enjoy and which you don't. You never have to fill anything in for this to happen. It builds up naturally as you use Cove.

**2. Figuring out *why* you like it.** A recommender takes that picture and looks closely at the content behind it: who's in it, what's happening, how it's shot, how it sounds. It works out the patterns in what you enjoy, then goes looking through your library for other things that match those patterns. Those are your recommendations.

Every recommendation comes with a breakdown of why it was picked, so you can see what drove it. You can also tune how much different components weigh in on the recommendations.

## The recommenders

You can switch between recommenders from a dropdown on the Recommended page. Try them and see which one fits you best.

- **Full taste model** is the main one. It judges every video on four separate things: performers, content, video quality, and audio. Each gets its own score and its own confidence, and the overall score is measured against what's typical for *your* library. This is the one to use most of the time.
- **Taste clusters** notices that most people like a few distinct "kinds" of thing, splits your taste into those groups, and recommends across them. It's a nice way to see how your own taste breaks up, and you can filter to one cluster at a time.
- **Overall affinity** is a simpler, single profile of the tags, looks, performers, and studios you like.
- **Your engagement** doesn't find anything new. It ranks what you've already watched and rated by how much you seem to have liked it. Useful before the other recommenders have much to go on, and a good way to check their results against what you actually watched.

## Using the Recommended page

The Recommended page works like any other list in Cove. Search, filters, sorting, display modes, multi-select, and bulk actions all work the same way. On top of that, the recommender adds its own scores as filters and sort options. For example, you can sort by Performers score, or set Sort to Random and filter to videos whose Content score is above a certain level to get a shuffle of just the stuff that matches your taste.

There are two more tabs at the top:

- **Training** shows you one item at a time, picked because the recommender has a strong guess about it but isn't sure yet. Rating these teaches it far faster than rating at random. Each item highlights the one aspect it most wants you to confirm. You can train on videos or on images. Images are quick, since a single frame is easy to judge, and image ratings feed the same model that drives your video recommendations.
- **Taste profile** shows what the recommender currently thinks you like and dislike: tags, performers, studios, and so on. If something looks wrong in here, rating a few things in Training is the fastest way to fix it.

Every video also gets a **Recommended** tab on its own page, listing the videos most similar to it according to your taste.

## Getting the most out of it

**It needs something to learn from.** If you've never watched or rated anything in Cove, the recommenders have no way of knowing what you like. Just using Cove normally builds this up over time. But if you want good results quickly:

1. **Rate, like, and favorite things.** Videos, images, performers, tags. Explicit ratings are the clearest signal you can give, and a handful of them goes a long way. The Training tab is the fastest route.
2. **Use aspect ratings.** Videos and images can be rated on specific aspects, not just overall:
   - **Content** is about what's actually happening in the video.
   - **Performers** is about who's in it.
   - **Audio** is about how the people sound and how much you like that.
   - **Video Quality** is about the camera work and picture quality. Is it shot well, with angles you like, or is the camera far away and hard to see?

   These matter because an overall rating only says *whether* you liked something, not *why*. Maybe you love a performer but hated a scene because the quality was bad or nothing interesting happened. Aspect ratings let the recommender tell those apart instead of guessing.
3. **Rate the things you didn't like too.** Low ratings are just as useful as high ones. A recommender that only knows your favorites can't tell what to stay away from.

**Settings.** Under the extension's settings you can set a "neutral point" for each kind of rating. Anything you rate above that point counts as a like, anything below counts as a dislike, and a rating right at it does nothing. If you tend to rate generously, raise the neutral so only your real favorites count as positive.

## About AI data

AI data (AI Visual, AI Tagging, AI Faces, AI Audio) makes a big difference. The recommenders work by understanding the content you liked in order to find more like it, and the more they can see about your content, the better they can do that.

- **AI Visual** is required.
- **AI Faces** improves the Performers aspect.
- **AI Audio** improves the Audio aspect.
- **AI Tagging** gives the content side much more to work with.

Anything you don't have just lowers the recommender's confidence in that area.

## Feedback

If recommendations look off, if something is confusing, or if you have an idea, please say so. Feedback from real use is what makes these better.

---

**For developers:** the recommenders above are built on **Recommendations: Core**, a library that handles the hard parts of a recommender for you: learning what a user likes from their engagement and ratings, access to content features, candidate generation, and the shared Recommended page UI. It installs automatically alongside Tastes. If you want to build your own recommender on top of it, use these recommenders as an example.
